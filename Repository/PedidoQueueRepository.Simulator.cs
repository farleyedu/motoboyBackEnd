using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Service;
using Dapper;

namespace APIBack.Repository
{
    /// <summary>Edicao completa de pedido para testes no simulador.</summary>
    public sealed partial class PedidoQueueRepository
    {
        private sealed class AddressRow
        {
            public string? Rua { get; set; }
            public string? Numero { get; set; }
            public string? Bairro { get; set; }
        }

        /// <summary>
        /// Reabre um pedido ja concluido ou cancelado, para simular que ele volta ao fluxo. Volta a
        /// PENDENTE e sem motoboy, nunca direto para atribuido/em rota: essas etapas exigem um motoboy e
        /// uma parada de rota, que so os comandos reais de fila (atribuir) criam. Assim o pedido nunca
        /// fica num estado que o resto do sistema nao sabe ler (em rota sem motoboy, na fila sem parada).
        /// </summary>
        public async Task<CreatedPedidoDto> ReopenPedidoForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var current = APIBack.Model.Enum.StatusPedidoExtensions.FromDbValue(pedido.StatusPedido)
                ?? APIBack.Model.Enum.StatusPedido.Pendente;
            if (!SimulatorOrderRules.CanReopen(current))
            {
                throw new DeliveryDomainException(409, "ORDER_NOT_REOPENABLE",
                    "So pedido concluido ou cancelado pode ser reaberto. Para os demais use os comandos de fila.");
            }

            // Concluido/cancelado nao tem parada ativa; por seguranca, qualquer resto vira "removed".
            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() " +
                "WHERE pedido_id = @PedidoId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN ('assigned', 'en_route');",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Pendente, motoboy_responsavel = NULL, horario_saida = NULL, horario_entrega = NULL " +
                "WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { Pendente = (int)APIBack.Model.Enum.StatusPedido.Pendente, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId },
                transaction);

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                pedidoId,
                action = "reopened",
                updatedByUserId = actorUserId,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryOrderUpdated,
                estabelecimentoId, motoboyId: null, aggregateVersion: pedidoId, payload, Array.Empty<Guid>());

            await transaction.CommitAsync();
            return new CreatedPedidoDto { Id = pedidoId, Status = "pendente" };
        }

        /// <summary>Reemite o evento de pedido no tempo real (o painel recarrega): "injeta" o pedido de teste no painel.</summary>
        public async Task PublishPedidoEventForSimulatorAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string action)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, action);
            await transaction.CommitAsync();
        }

        public async Task<CreatedPedidoDto> UpdatePedidoForSimulatorAsync(
            Guid estabelecimentoId, int actorUserId, int pedidoId, SimulatorOrderPatch patch)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var current = await connection.QuerySingleOrDefaultAsync<AddressRow?>(@"
SELECT entrega_rua::text AS Rua, entrega_numero::text AS Numero, entrega_bairro::text AS Bairro
  FROM pedido
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId
 FOR UPDATE;", new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var parameters = new DynamicParameters();
            parameters.Add("PedidoId", pedidoId);
            parameters.Add("EstabelecimentoId", estabelecimentoId);
            var sets = new System.Collections.Generic.List<string>();

            // Nomes de coluna vem SO de SimulatorOrderRules (lista fixa), nunca do cliente.
            var index = 0;
            foreach (var column in patch.Columns)
            {
                var name = $"P{index++}";
                sets.Add($"{column.Column} = @{name}");
                parameters.Add(name, column.Value);
            }

            if (patch.TouchesAddress)
            {
                // Recompoe o endereco com o que mudou por cima do que ja estava.
                var rua = patch.Rua ?? (patch.Columns.Any(c => c.Column == "entrega_rua") ? null : current.Rua);
                var numero = patch.Numero ?? (patch.Columns.Any(c => c.Column == "entrega_numero") ? null : current.Numero);
                var bairro = patch.Bairro ?? (patch.Columns.Any(c => c.Column == "entrega_bairro") ? null : current.Bairro);
                sets.Add("endereco_entrega = @EnderecoEntrega");
                parameters.Add("EnderecoEntrega", SimulatorOrderRules.ComposeAddress(rua, numero, patch.Complemento, bairro));
            }

            if (patch.PrevisaoEmMinutos.HasValue)
            {
                var expression = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMin");
                sets.Add($"previsao_entrega = {expression}");
                parameters.Add("PrevisaoMin", patch.PrevisaoEmMinutos.Value);
            }

            if (patch.PedidoHaMinutos.HasValue)
            {
                var dataPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "data_pedido", "@PedidoMin");
                var horarioPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_pedido", "@PedidoMin");
                sets.Add($"data_pedido = {dataPedido}");
                sets.Add($"horario_pedido = {horarioPedido}");
                parameters.Add("PedidoMin", -patch.PedidoHaMinutos.Value);
            }

            if (sets.Count > 0)
            {
                await connection.ExecuteAsync(
                    $"UPDATE pedido SET {string.Join(", ", sets)} WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                    parameters, transaction);
            }

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                pedidoId,
                action = "updated",
                updatedByUserId = actorUserId,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryOrderUpdated,
                estabelecimentoId, motoboyId: null, aggregateVersion: pedidoId, payload, Array.Empty<Guid>());

            await transaction.CommitAsync();
            return new CreatedPedidoDto { Id = pedidoId };
        }
    }
}
