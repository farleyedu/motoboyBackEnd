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
