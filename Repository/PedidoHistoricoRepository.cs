using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class PedidoHistoricoRepository : IPedidoHistoricoRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public PedidoHistoricoRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public async Task<PedidoHistoricoDto?> GetAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var args = new { EstabelecimentoId = estabelecimentoId, PedidoId = pedidoId };

            // Coluna legada de data: lida como texto e convertida com seguranca em C#.
            var pedido = await connection.QuerySingleOrDefaultAsync<PedidoCreatedRow>(
                "SELECT p.id AS Id, p.data_pedido::text AS DataPedidoRaw, p.horario_pedido::text AS HorarioPedidoRaw " +
                "FROM pedido p WHERE p.id = @PedidoId AND p.id_estabelecimento = @EstabelecimentoId;",
                args);
            if (pedido is null) return null;

            var stops = (await connection.QueryAsync<PedidoHistoricoStopRow>(@"
SELECT s.id AS Id,
       s.motoboy_id AS MotoboyId,
       m.nome::text AS MotoboyNome,
       s.stop_status AS StopStatus,
       s.assigned_at_utc AS AssignedAtUtc,
       s.started_at_utc AS StartedAtUtc,
       s.picked_up_at_utc AS PickedUpAtUtc,
       s.arrived_at_utc AS ArrivedAtUtc,
       s.completed_at_utc AS CompletedAtUtc,
       s.failed_at_utc AS FailedAtUtc,
       s.refused_at_utc AS RefusedAtUtc,
       s.removed_at_utc AS RemovedAtUtc,
       s.canceled_at_utc AS CanceledAtUtc,
       s.transferred_at_utc AS TransferredAtUtc,
       s.transfer_request_id AS TransferRequestId,
       s.failure_reason AS FailureReason,
       s.refusal_reason AS RefusalReason,
       s.cancel_reason AS CancelReason,
       s.completed_by AS CompletedBy
  FROM delivery_route_stops s
  LEFT JOIN motoboy m ON m.id = s.motoboy_id
 WHERE s.pedido_id = @PedidoId
   AND s.estabelecimento_id = @EstabelecimentoId
 ORDER BY s.assigned_at_utc, s.id;", args)).ToList();

            var transfers = (await connection.QueryAsync<PedidoHistoricoTransferRow>(@"
SELECT t.id AS Id,
       t.from_motoboy_id AS FromMotoboyId,
       mf.nome::text AS FromMotoboyNome,
       t.to_motoboy_id AS ToMotoboyId,
       mt.nome::text AS ToMotoboyNome,
       t.status AS Status,
       t.requested_by AS RequestedBy,
       t.reason AS Reason,
       t.requested_at_utc AS RequestedAtUtc,
       t.decided_at_utc AS DecidedAtUtc,
       t.completed_at_utc AS CompletedAtUtc,
       t.decision_note AS DecisionNote
  FROM delivery_transfer_requests t
  LEFT JOIN motoboy mf ON mf.id = t.from_motoboy_id
  LEFT JOIN motoboy mt ON mt.id = t.to_motoboy_id
 WHERE t.pedido_id = @PedidoId
   AND t.estabelecimento_id = @EstabelecimentoId
 ORDER BY t.requested_at_utc, t.id;", args)).ToList();

            var created = DeliveryRules.ParseStoredDateTime(pedido.HorarioPedidoRaw)
                ?? DeliveryRules.ParseStoredDateTime(pedido.DataPedidoRaw);

            return PedidoHistoricoBuilder.Build(pedidoId, ToOffset(created), stops, transfers);
        }

        // Sem fuso na coluna legada = hora local do estabelecimento (mesma regra do map-state).
        private static DateTimeOffset? ToOffset(DateTime? value)
        {
            if (!value.HasValue) return null;
            return value.Value.Kind == DateTimeKind.Utc
                ? new DateTimeOffset(value.Value)
                : OperationalDayWindow.LocalToUtc(value.Value);
        }

        private sealed class PedidoCreatedRow
        {
            public int Id { get; set; }
            public string? DataPedidoRaw { get; set; }
            public string? HorarioPedidoRaw { get; set; }
        }
    }
}
