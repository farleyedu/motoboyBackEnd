using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Acoes executadas pelo proprio motoboy (app ou simulador). O motoboy e o
    /// estabelecimento sempre vem do token operacional; cada acao revalida que a
    /// parada pertence a ele.
    /// </summary>
    public sealed partial class PedidoQueueRepository
    {
        public Task<MotoboyQueueDto> MarkPickedUpAsync(Guid estabelecimentoId, int motoboyId) =>
            MarkCurrentMilestoneAsync(estabelecimentoId, motoboyId, "picked_up_at_utc", "picked_up");

        public Task<MotoboyQueueDto> MarkArrivedAsync(Guid estabelecimentoId, int motoboyId) =>
            MarkCurrentMilestoneAsync(estabelecimentoId, motoboyId, "arrived_at_utc", "arrived");

        public Task<MotoboyQueueDto> MarkArrivedForPedidoAsync(Guid estabelecimentoId, int motoboyId, int expectedPedidoId) =>
            MarkCurrentMilestoneAsync(estabelecimentoId, motoboyId, "arrived_at_utc", "arrived", expectedPedidoId);

        /// <summary>
        /// Coletei / cheguei: marco na entrega atual. Repetir a acao nao altera o horario
        /// ja gravado (idempotente para reenvio do app).
        /// </summary>
        private async Task<MotoboyQueueDto> MarkCurrentMilestoneAsync(
            Guid estabelecimentoId, int motoboyId, string column, string action, int? expectedPedidoId = null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var version = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var current = await GetCurrentStopAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true)
                ?? throw new DeliveryDomainException(409, "NO_CURRENT_DELIVERY", "Voce nao tem entrega atual em rota.");

            DeliveryArrivalRules.EnsureExpectedPedido(expectedPedidoId, current.PedidoId);

            var alreadyMarked = column == "picked_up_at_utc" ? current.PickedUpAtUtc.HasValue : current.ArrivedAtUtc.HasValue;
            if (!alreadyMarked)
            {
                // column vem de constante interna, nunca de entrada do usuario.
                await connection.ExecuteAsync(
                    $"UPDATE delivery_route_stops SET {column} = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                    new { current.Id }, transaction);
                version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
                var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
                await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, current.PedidoId, action, version, sessionId);
            }

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> DeliverCurrentAsync(Guid estabelecimentoId, int motoboyId, string? codigo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var snapshot = await CompleteCurrentInternalAsync(connection, transaction, estabelecimentoId, motoboyId,
                completedBy: "motoboy", providedCode: codigo, enforceCode: true);
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>
        /// Nao consegui entregar: a parada vira 'failed' com o motivo e o pedido volta a
        /// Pendente (sem motoboy) para o atendente decidir. O proximo da fila assume.
        /// </summary>
        public async Task<MotoboyQueueDto> FailCurrentAsync(Guid estabelecimentoId, int motoboyId, string motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var current = await GetCurrentStopAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true)
                ?? throw new DeliveryDomainException(409, "NO_CURRENT_DELIVERY", "Voce nao tem entrega atual em rota.");

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'failed', failed_at_utc = NOW(), failure_reason = @Motivo, updated_at_utc = NOW() WHERE id = @Id;",
                new { current.Id, Motivo = motivo }, transaction);
            await ReturnPedidoToPendingAsync(connection, transaction, current.PedidoId);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, current.PedidoId,
                "Entrega nao realizada pelo motoboy.");

            var snapshot = await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, motoboyId,
                current.PedidoId, "failed", promoteNext: true);
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>
        /// Recusar: devolve um pedido ainda nao coletado. Depois de coletar o motoboy ja
        /// esta com o pedido em maos -- nesse caso a saida e "nao entregue" ou transferir.
        /// </summary>
        public async Task<MotoboyQueueDto> RefuseAsync(Guid estabelecimentoId, int motoboyId, int pedidoId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            if (!settings.AllowMotoboyRefuse)
            {
                throw new DeliveryDomainException(403, "REFUSE_NOT_ALLOWED", "Este estabelecimento nao permite recusar pedidos.");
            }

            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != motoboyId)
            {
                throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_YOUR_QUEUE", "Este pedido nao esta na sua fila.");
            }
            RouteLockRules.EnsureNotLockedForMotoboy(await IsStopLockedAsync(connection, transaction, stop.Id), pedidoId, "recusado");
            if (stop.PickedUpAtUtc.HasValue)
            {
                throw new DeliveryDomainException(409, "ALREADY_PICKED_UP",
                    "O pedido ja foi coletado. Use 'nao consegui entregar' ou transfira para outro motoboy.");
            }

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'refused', refused_at_utc = NOW(), refusal_reason = @Motivo, updated_at_utc = NOW() WHERE id = @Id;",
                new { stop.Id, Motivo = motivo }, transaction);
            await ReturnPedidoToPendingAsync(connection, transaction, pedidoId);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId,
                "Pedido recusado pelo motoboy.");

            var snapshot = await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, motoboyId,
                pedidoId, "refused", promoteNext: stop.StopStatus == "en_route");
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>
        /// Recusa a oferta inteira antes da coleta. Diferente da recusa individual,
        /// uma ancora nao obriga o motoboy a aceitar toda a rota: o lote completo volta
        /// ao estabelecimento em uma unica transacao.
        /// </summary>
        public async Task<MotoboyQueueDto> RefuseRouteByMotoboyAsync(
            Guid estabelecimentoId, int motoboyId, IReadOnlyList<int> pedidoIds, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            if (!settings.AllowMotoboyRefuse)
            {
                throw new DeliveryDomainException(403, "REFUSE_NOT_ALLOWED", "Este estabelecimento nao permite recusar rotas.");
            }

            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var activeStops = await GetActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true);
            var requested = pedidoIds.ToHashSet();
            var stops = activeStops.Where(stop => requested.Contains(stop.PedidoId)).ToList();
            var missing = pedidoIds.Where(id => stops.All(stop => stop.PedidoId != id)).ToList();
            if (missing.Count > 0)
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A rota mudou antes da recusa. Recarregue e tente novamente.", new { pedidoIds = missing });
            }
            if (stops.Any(stop => stop.PickedUpAtUtc.HasValue))
            {
                throw new DeliveryDomainException(409, "ALREADY_PICKED_UP", "A rota ja possui pedido coletado e nao pode mais ser recusada.");
            }

            await connection.ExecuteAsync(@"
UPDATE delivery_route_stops
   SET stop_status = 'refused', refused_at_utc = NOW(), refusal_reason = @Motivo, updated_at_utc = NOW()
 WHERE id = ANY(@StopIds);",
                new { StopIds = stops.Select(stop => stop.Id).ToArray(), Motivo = motivo }, transaction);
            foreach (var stop in stops)
            {
                await ReturnPedidoToPendingAsync(connection, transaction, stop.PedidoId);
                await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, stop.PedidoId,
                    "Rota recusada pelo motoboy.");
            }

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (stops.Any(stop => stop.StopStatus == "en_route") && eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "route_refused", version, eligibility.SessionId);
            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> ReorderByMotoboyAsync(
            Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            if (!settings.AllowMotoboyReorder)
            {
                throw new DeliveryDomainException(403, "REORDER_NOT_ALLOWED", "Este estabelecimento nao permite reordenar a fila.");
            }

            var snapshot = await ReorderInternalAsync(connection, transaction, estabelecimentoId, motoboyId, expectedVersion, pedidoIdsOrdenados);
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>
        /// Confirma a oferta na ordem escolhida. Antes da coleta a primeira parada ainda
        /// pode mudar; nesse caso o marcador en_route passa atomicamente para a nova
        /// primeira entrega. Depois da coleta, a entrega atual continua fixa.
        /// </summary>
        public async Task<MotoboyQueueDto> AcceptRouteByMotoboyAsync(
            Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            if (currentVersion != expectedVersion)
            {
                throw new DeliveryDomainException(409, "QUEUE_VERSION_CONFLICT",
                    "A rota foi modificada. Confira a nova sequencia antes de aceitar.", new { currentVersion });
            }

            var activeStops = await GetActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true);
            if (!activeStops.Select(stop => stop.PedidoId).ToHashSet().SetEquals(pedidoIdsOrdenados))
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH", "A lista enviada nao corresponde a rota atual.");
            }

            var changed = activeStops.Select(stop => stop.PedidoId)
                .Where((id, index) => pedidoIdsOrdenados[index] != id)
                .Any();
            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            if (changed && !settings.AllowMotoboyReorder)
            {
                throw new DeliveryDomainException(403, "REORDER_NOT_ALLOWED", "Este estabelecimento nao permite reordenar a fila.");
            }

            var current = activeStops.FirstOrDefault(stop => stop.StopStatus == "en_route");
            if (current?.PickedUpAtUtc.HasValue == true && pedidoIdsOrdenados[0] != current.PedidoId)
            {
                throw new DeliveryDomainException(409, "ALREADY_PICKED_UP", "A entrega coletada precisa continuar na primeira posicao.");
            }

            var lockedIds = await GetLockedPedidoIdsAsync(connection, transaction, estabelecimentoId, motoboyId);
            RouteLockRules.ValidateReorder(
                activeStops.Select(stop => new RouteSlot(stop.PedidoId, lockedIds.Contains(stop.PedidoId), stop.StopStatus == "en_route")).ToList(),
                pedidoIdsOrdenados);

            if (changed)
            {
                if (current != null && pedidoIdsOrdenados[0] != current.PedidoId)
                {
                    await connection.ExecuteAsync(
                        "UPDATE delivery_route_stops SET stop_status = 'assigned', started_at_utc = NULL, updated_at_utc = NOW() WHERE id = @Id;",
                        new { current.Id }, transaction);
                    await connection.ExecuteAsync(
                        "UPDATE pedido SET status_pedido = @Atribuido, horario_saida = NULL WHERE id = @PedidoId;",
                        new { Atribuido = (int)StatusPedido.Atribuido, current.PedidoId }, transaction);
                }

                await ApplyPositionsAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoIdsOrdenados);
                if (current == null || pedidoIdsOrdenados[0] != current.PedidoId)
                {
                    var newCurrent = activeStops.Single(stop => stop.PedidoId == pedidoIdsOrdenados[0]);
                    await connection.ExecuteAsync(
                        "UPDATE delivery_route_stops SET stop_status = 'en_route', started_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                        new { newCurrent.Id }, transaction);
                    var saidaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_saida");
                    await connection.ExecuteAsync(
                        $"UPDATE pedido SET status_pedido = @EmRota, horario_saida = {saidaNow} WHERE id = @PedidoId;",
                        new { EmRota = (int)StatusPedido.EmRota, newCurrent.PedidoId }, transaction);
                }
            }

            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "route_accepted", version, sessionId);
            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>
        /// Fecha uma parada que saiu da fila (falha, recusa, transferencia de saida):
        /// promove o proximo quando era a entrega atual, renumera, versiona e avisa.
        /// A fila ja esta travada por quem chama.
        /// </summary>
        private async Task<MotoboyQueueDto> FinishStopRemovalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            Guid estabelecimentoId, int motoboyId, int pedidoId, string action, bool promoteNext)
        {
            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (promoteNext && eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var entersReturn = await EnterReturningIfIdleAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, action, version, eligibility.SessionId);
            if (entersReturn)
            {
                await EmitRouteStateEventAsync(connection, transaction, estabelecimentoId, motoboyId,
                    DeliveryRealtimeEvents.DeliveryRouteReturning, RouteStates.Returning, "delivery", version, eligibility.SessionId);
            }
            return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        }
    }
}
