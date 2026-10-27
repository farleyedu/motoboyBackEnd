using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>Parada afetada pelo encerramento do turno de um motoboy.</summary>
    internal sealed class DisconnectedStop
    {
        public long StopId { get; set; }
        public int PedidoId { get; set; }
        public string StopStatus { get; set; } = "assigned";
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        public int? PedidoStatus { get; set; }
        public string? Value { get; set; }
        public string? TipoPagamento { get; set; }
        public string? StatusPagamento { get; set; }
        public string? Troco { get; set; }
        /// <summary>Coletado = a mercadoria esta com o motoboy; nao volta sozinha para a fila.</summary>
        public bool Collected => PickedUpAtUtc.HasValue;
    }

    public sealed partial class PedidoQueueRepository
    {
        /// <summary>
        /// Encerramento do turno (inatividade, saida, substituicao, revogacao), dentro da transacao de quem encerra:
        /// trava a fila do motoboy, devolve ao restaurante o que ainda nao foi retirado e deixa o que ja foi coletado
        /// exatamente como esta (parada, responsavel e status), sem simular retorno a loja. Devolve todas as paradas afetadas.
        /// </summary>
        internal static async Task<List<DisconnectedStop>> CaptureAndReleaseOnDisconnectAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var preview = (await connection.QueryAsync<int>(
                $"SELECT s.pedido_id FROM delivery_route_stops s WHERE s.motoboy_id = @M AND s.estabelecimento_id = @E AND s.stop_status IN {ActiveStatusesSql};",
                new { M = motoboyId, E = estabelecimentoId }, transaction)).ToArray();
            if (preview.Length == 0) return new List<DisconnectedStop>();

            // Mesma ordem dos demais comandos: cabecalho da fila, pedidos (por id) e so entao as paradas.
            await connection.ExecuteAsync(
                "SELECT id FROM pedido WHERE id = ANY(@Ids) ORDER BY id FOR UPDATE;", new { Ids = preview }, transaction);
            var stops = (await connection.QueryAsync<DisconnectedStop>($@"
SELECT s.id AS StopId, s.pedido_id AS PedidoId, s.stop_status AS StopStatus, s.picked_up_at_utc AS PickedUpAtUtc,
       p.status_pedido AS PedidoStatus, p.value::text AS Value, p.tipo_pagamento::text AS TipoPagamento,
       p.status_pagamento::text AS StatusPagamento, p.troco::text AS Troco
  FROM delivery_route_stops s
  JOIN pedido p ON p.id = s.pedido_id
 WHERE s.motoboy_id = @M AND s.estabelecimento_id = @E AND s.stop_status IN {ActiveStatusesSql}
 ORDER BY s.position
   FOR UPDATE OF s;", new { M = motoboyId, E = estabelecimentoId }, transaction)).ToList();

            var released = new List<int>();
            foreach (var stop in stops)
            {
                await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, stop.PedidoId,
                    "Turno do motoboy encerrado.");
                if (stop.Collected) continue;
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                    new { Id = stop.StopId }, transaction);
                await ReturnPedidoToPendingAsync(connection, transaction, stop.PedidoId);
                released.Add(stop.PedidoId);
            }

            if (released.Count > 0)
            {
                await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
                var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
                foreach (var pedidoId in released)
                    await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "removed", version, null);
            }
            return stops;
        }

        /// <summary>
        /// Incidente de desconexao: a loja confirmou que a mercadoria coletada voltou fisicamente. A parada sai da fila do motoboy
        /// anterior e o pedido volta a pendente (sob gestao do restaurante). Idempotente; nao mexe em pedido concluido/cancelado.
        /// </summary>
        public async Task<MotoboyQueueDto?> ReleaseCollectedAfterReturnAsync(
            Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false);
            if (preview == null)
            {
                var current = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
                    ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
                if (current.StatusPedido == (int)Model.Enum.StatusPedido.Pendente) { await transaction.CommitAsync(); return null; }
                throw new DeliveryDomainException(409, "PEDIDO_NOT_IN_QUEUE", "Pedido nao esta mais na fila do motoboy.");
            }

            var motoboyId = preview.MotoboyId;
            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            _ = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true, allowIfoodRouting: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != motoboyId)
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila do pedido mudou. Recarregue e tente novamente.");

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { stop.Id }, transaction);
            await ReturnPedidoToPendingAsync(connection, transaction, pedidoId);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId,
                "Devolucao fisica confirmada pelo atendente.");
            await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId,
                "returned_to_store", "operador_incidente", motivo);

            var snapshot = await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, motoboyId,
                pedidoId, "removed", promoteNext: stop.StopStatus == "en_route");
            await transaction.CommitAsync();
            return snapshot;
        }
    }
}
