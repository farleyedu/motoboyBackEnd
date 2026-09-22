using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;

namespace APIBack.Repository
{
    /// <summary>Pedido criado manualmente pelo atendente.</summary>
    public sealed partial class PedidoQueueRepository
    {
        public async Task<CreatedPedidoDto> CreatePedidoAsync(Guid estabelecimentoId, int actorUserId, ManualOrder order)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            // Horarios na hora local de Brasilia, no formato certo para o tipo de cada coluna.
            var dataPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "data_pedido");
            var horarioPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_pedido");
            var previsao = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMinutos");

            var pedidoId = await connection.ExecuteScalarAsync<int>($@"
INSERT INTO pedido (
    nome_cliente, endereco_entrega, telefone_cliente, data_pedido, status_pedido,
    items, value, region, latitude, longitude, horario_pedido, previsao_entrega,
    entrega_rua, entrega_numero, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep,
    tipo_pagamento, troco, observacoes, status_pagamento, codigo_entrega, id_estabelecimento)
VALUES (
    @NomeCliente, @EnderecoEntrega, @TelefoneCliente, {dataPedido}, @Pendente,
    @Items, @Value, @Bairro, @Latitude, @Longitude, {horarioPedido}, {previsao},
    @Rua, @Numero, @Bairro, @Cidade, @Estado, @Cep,
    @TipoPagamento, @Troco, @Observacoes, 'Pendente', @CodigoEntrega, @EstabelecimentoId)
RETURNING id;",
                new
                {
                    order.NomeCliente,
                    order.EnderecoEntrega,
                    order.TelefoneCliente,
                    Pendente = (int)StatusPedido.Pendente,
                    order.Items,
                    order.Value,
                    order.Bairro,
                    order.Latitude,
                    order.Longitude,
                    order.PrevisaoMinutos,
                    order.Rua,
                    order.Numero,
                    order.Cidade,
                    order.Estado,
                    order.Cep,
                    order.TipoPagamento,
                    order.Troco,
                    order.Observacoes,
                    order.CodigoEntrega,
                    EstabelecimentoId = estabelecimentoId
                }, transaction);

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                pedidoId,
                action = "created",
                createdByUserId = actorUserId,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryOrderUpdated,
                estabelecimentoId, motoboyId: null, aggregateVersion: pedidoId, payload, Array.Empty<Guid>());

            await transaction.CommitAsync();
            return new CreatedPedidoDto { Id = pedidoId };
        }
    }
}
