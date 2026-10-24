using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

/// <summary>
/// Acoes do operador sobre a fila que o app do motoboy nao cobre: concluir uma entrega que nao e a atual e passar a rota
/// inteira para outro motoboy. Seguem as regras de concorrencia do cabecalho de <see cref="PedidoQueueRepository"/>.
/// </summary>
public sealed partial class PedidoQueueRepository
{
    /// <summary>Oferta enviada e ainda sem resposta do motoboy: o pedido nao esta de fato com ele.</summary>
    private static async Task<bool> IsOfferPendingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long stopId) =>
        await PedidoColumnTypes.HasOfertaSchemaAsync(connection, transaction) &&
        await connection.ExecuteScalarAsync<bool>("SELECT offered_at_utc IS NOT NULL FROM delivery_route_stops WHERE id = @Id;", new { Id = stopId }, transaction);

    /// <summary>
    /// O operador conclui uma entrega especifica (o motoboy esqueceu de marcar). Mesma conclusao e mesmo registro de remuneracao
    /// de <see cref="CompleteCurrentAsync"/>, sem mexer na entrega atual. Pedido iFood continua pelo fluxo oficial.
    /// </summary>
    public async Task<MotoboyQueueDto> CompleteStopAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var preview = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
            ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_QUEUE", "Pedido nao esta na fila de nenhum motoboy.");
        var snapshot = await CompleteCurrentInternalAsync(connection, transaction, estabelecimentoId, preview.MotoboyId,
            completedBy: "operator", providedCode: null, enforceCode: false, targetPedidoId: pedidoId);
        await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId,
            "operator_delivered", "operador_rota", motivo ?? "Entrega concluida pelo operador.", preview.MotoboyId);
        await transaction.CommitAsync();
        return snapshot;
    }

    /// <summary>
    /// Passa a rota inteira (todas as paradas ativas, na ordem) para outro motoboy numa transacao so: tudo ou nada. O destino
    /// recebe uma unica oferta; pedidos ja coletados levam a coleta junto. A origem renumera, versiona e avisa uma vez.
    /// </summary>
    public async Task<RouteTransferResultDto> TransferRouteByOperatorAsync(
        Guid estabelecimentoId, int operatorUserId, int fromMotoboyId, int toMotoboyId, string? motivo)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await LockQueuesAsync(connection, transaction, estabelecimentoId, fromMotoboyId, toMotoboyId);
        var stops = await GetActiveStopsAsync(connection, transaction, estabelecimentoId, fromMotoboyId, forUpdate: true);
        if (stops.Count == 0)
            throw new DeliveryDomainException(409, "ROUTE_EMPTY", "Este motoboy nao tem pedidos na fila para transferir.");
        await EnsureEligibleAsync(connection, transaction, toMotoboyId, estabelecimentoId);
        var offerId = await NewOfferIdIfRequiredAsync(connection, transaction, estabelecimentoId);
        await EnsureNoOtherPendingOfferAsync(connection, transaction, estabelecimentoId, toMotoboyId, offerId);

        var moved = new List<(long RequestId, StopRow Stop)>();
        foreach (var stop in stops)
        {
            try
            {
                // Pedido iFood bloqueado (revisao, dado desatualizado) barra a rota com o motivo, antes da recusa do banco.
                _ = await GetPedidoAsync(connection, transaction, estabelecimentoId, stop.PedidoId, forUpdate: true, allowIfoodRouting: true);
            }
            catch (DeliveryDomainException ex)
            {
                throw new DeliveryDomainException(ex.StatusCode, ex.Code, $"Pedido {stop.PedidoId}: {ex.Message}");
            }
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, stop.PedidoId,
                "Substituida por transferencia da rota feita pelo atendente.");
            var requestId = await InsertTransferAsync(connection, transaction, estabelecimentoId, stop.PedidoId,
                fromMotoboyId, toMotoboyId, TransferStatuses.Completed, TransferPolicies.Operator, "operator", operatorUserId, motivo, operatorUserId);
            await connection.ExecuteAsync(@"
UPDATE delivery_route_stops
   SET stop_status = 'transferred', transferred_at_utc = NOW(), transfer_request_id = @RequestId, updated_at_utc = NOW()
 WHERE id = @Id;", new { stop.Id, RequestId = requestId }, transaction);
            moved.Add((requestId, stop));
        }

        // A rota inteira sai: nao promove ninguem na origem.
        var sourceQueue = await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, fromMotoboyId,
            stops[0].PedidoId, "transferred_out", promoteNext: false);

        long toVersion = 0;
        foreach (var (requestId, stop) in moved)
        {
            toVersion = await AppendStopAsync(connection, transaction, estabelecimentoId, toMotoboyId, stop.PedidoId,
                actorUserId: null, pickedUpAtUtc: stop.PickedUpAtUtc, transferRequestId: requestId, offerId: offerId);
            await EmitTransferEventAsync(connection, transaction, estabelecimentoId, new TransferEventRow
            {
                Id = requestId, PedidoId = stop.PedidoId, FromMotoboyId = fromMotoboyId, ToMotoboyId = toMotoboyId, Status = TransferStatuses.Completed
            });
        }
        var toSession = await GetSessionIdAsync(connection, transaction, toMotoboyId, estabelecimentoId);
        await EmitQueueEventAsync(connection, transaction, estabelecimentoId, toMotoboyId, stops[0].PedidoId, "offered", toVersion, toSession);

        var result = new RouteTransferResultDto
        {
            SourceQueue = sourceQueue,
            TargetQueue = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, toMotoboyId, toVersion)
        };
        foreach (var (requestId, _) in moved)
            result.Transfers.Add(await GetTransferAsync(connection, transaction, estabelecimentoId, requestId));
        await transaction.CommitAsync();
        return result;
    }
}
