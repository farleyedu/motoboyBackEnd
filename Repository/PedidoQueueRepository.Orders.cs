using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>Criacao, edicao e confirmacao de pedido (nucleo de pedido, Fase 2).</summary>
    public sealed partial class PedidoQueueRepository
    {
        private const string UniqueViolation = "23505";
        private static readonly JsonSerializerOptions ItemJson = new(JsonSerializerDefaults.Web);

        public async Task<CreatedPedidoDto> CreatePedidoAsync(Guid estabelecimentoId, int actorUserId, ManualOrder order)
        {
            try
            {
                return await CreatePedidoCoreAsync(estabelecimentoId, actorUserId, order);
            }
            catch (PostgresException ex) when (ex.SqlState == UniqueViolation && !string.IsNullOrEmpty(order.OrigemRef))
            {
                // Duas chamadas simultaneas com a mesma origem/origem_ref: o indice unico deixou passar
                // so uma. A outra devolve o pedido que ganhou (idempotencia sob concorrencia).
                await using var connection = await _dataSource.OpenConnectionAsync();
                var existing = await FindByOrigemRefAsync(connection, null, estabelecimentoId, order);
                if (existing != null) return existing;
                throw;
            }
        }

        private async Task<CreatedPedidoDto> CreatePedidoCoreAsync(Guid estabelecimentoId, int actorUserId, ManualOrder order)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var coreSchema = await PedidoColumnTypes.HasCoreSchemaAsync(connection, transaction);
            if (!coreSchema && order.NeedsCoreSchema)
            {
                throw new DeliveryDomainException(503, "MIGRATION_PENDING",
                    "Este recurso exige as migracoes 20260925_* do delivery, ainda nao aplicadas neste banco.");
            }

            if (coreSchema && !string.IsNullOrEmpty(order.OrigemRef))
            {
                var existing = await FindByOrigemRefAsync(connection, transaction, estabelecimentoId, order);
                if (existing != null)
                {
                    await transaction.CommitAsync();
                    return existing;
                }
            }

            // Horarios na hora local de Brasilia, no formato certo para o tipo de cada coluna.
            var dataPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "data_pedido");
            var horarioPedido = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_pedido");
            var previsao = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMinutos");

            var extraColumns = coreSchema ? ", origem, origem_ref, conversa_id, subtotal, taxa_entrega, desconto" : string.Empty;
            var extraValues = coreSchema ? ", @Origem, @OrigemRef, @ConversaId, @Subtotal, @TaxaEntrega, @Desconto" : string.Empty;
            var status = order.Rascunho ? StatusPedido.Rascunho : StatusPedido.Pendente;

            var pedidoId = await connection.ExecuteScalarAsync<int>($@"
INSERT INTO pedido (
    nome_cliente, endereco_entrega, telefone_cliente, data_pedido, status_pedido,
    items, value, region, latitude, longitude, horario_pedido, previsao_entrega,
    entrega_rua, entrega_numero, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep,
    tipo_pagamento, troco, observacoes, status_pagamento, codigo_entrega, id_estabelecimento{extraColumns})
VALUES (
    @NomeCliente, @EnderecoEntrega, @TelefoneCliente, {dataPedido}, @Status,
    @Items, @Value, @Bairro, @Latitude, @Longitude, {horarioPedido}, {previsao},
    @Rua, @Numero, @Bairro, @Cidade, @Estado, @Cep,
    @TipoPagamento, @Troco, @Observacoes, 'Pendente', @CodigoEntrega, @EstabelecimentoId{extraValues})
RETURNING id;",
                InsertParameters(estabelecimentoId, order, status), transaction);

            if (coreSchema && order.Lines.Count > 0)
            {
                await InsertItemsAsync(connection, transaction, pedidoId, order);
            }

            if (!order.Rascunho)
            {
                await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "created");
            }

            await transaction.CommitAsync();
            return ToCreatedDto(pedidoId, status, order, jaExistia: false);
        }

        public async Task<CreatedPedidoDto> UpdatePedidoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, ManualOrder order)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            if (!await PedidoColumnTypes.HasCoreSchemaAsync(connection, transaction))
            {
                throw new DeliveryDomainException(503, "MIGRATION_PENDING",
                    "Editar pedido exige as migracoes 20260925_* do delivery, ainda nao aplicadas neste banco.");
            }

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var current = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
            if ((current != StatusPedido.Rascunho && current != StatusPedido.Pendente) || pedido.MotoboyResponsavel.HasValue)
            {
                throw new DeliveryDomainException(409, "ORDER_NOT_EDITABLE",
                    "So e possivel editar pedido em rascunho ou pendente e sem motoboy.");
            }
            if (current == StatusPedido.Pendente && order.Rascunho)
            {
                throw new DeliveryDomainException(409, "ORDER_NOT_EDITABLE", "Pedido pendente nao volta a rascunho.");
            }

            var status = order.Rascunho ? StatusPedido.Rascunho : StatusPedido.Pendente;
            var previsao = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMinutos");

            await connection.ExecuteAsync($@"
UPDATE pedido
   SET nome_cliente = @NomeCliente, endereco_entrega = @EnderecoEntrega, telefone_cliente = @TelefoneCliente,
       status_pedido = @Status, items = @Items, value = @Value, region = @Bairro,
       latitude = @Latitude, longitude = @Longitude, previsao_entrega = {previsao},
       entrega_rua = @Rua, entrega_numero = @Numero, entrega_bairro = @Bairro, entrega_cidade = @Cidade,
       entrega_estado = @Estado, entrega_cep = @Cep, tipo_pagamento = @TipoPagamento, troco = @Troco,
       observacoes = @Observacoes, codigo_entrega = @CodigoEntrega,
       conversa_id = COALESCE(@ConversaId, conversa_id),
       subtotal = @Subtotal, taxa_entrega = @TaxaEntrega, desconto = @Desconto
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;", InsertParameters(estabelecimentoId, order, status, pedidoId), transaction);

            await connection.ExecuteAsync("DELETE FROM pedido_item WHERE pedido_id = @PedidoId;", new { PedidoId = pedidoId }, transaction);
            if (order.Lines.Count > 0)
            {
                await InsertItemsAsync(connection, transaction, pedidoId, order);
            }

            if (!order.Rascunho)
            {
                await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "updated");
            }

            await transaction.CommitAsync();
            return ToCreatedDto(pedidoId, status, order, jaExistia: false);
        }

        public async Task<CreatedPedidoDto> ConfirmPedidoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, int previsaoMinutos)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var current = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;

            if (current != StatusPedido.Rascunho)
            {
                // Idempotente: confirmar de novo um pedido que ja saiu do rascunho nao muda nada.
                if (current == StatusPedido.Cancelado)
                {
                    throw new DeliveryDomainException(409, "ORDER_NOT_CONFIRMABLE", "Pedido cancelado nao pode ser confirmado.");
                }
                await transaction.CommitAsync();
                return new CreatedPedidoDto { Id = pedidoId, Status = ToStatusKey(current), JaExistia = true };
            }

            // A previsao passa a contar da confirmacao, nao da criacao do rascunho.
            var previsao = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMinutos");
            await connection.ExecuteAsync($@"
UPDATE pedido
   SET status_pedido = @Pendente, previsao_entrega = {previsao}
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { Pendente = (int)StatusPedido.Pendente, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId, PrevisaoMinutos = previsaoMinutos },
                transaction);

            await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "confirmed");
            await transaction.CommitAsync();
            return new CreatedPedidoDto { Id = pedidoId, Status = "pendente" };
        }

        // ---- auxiliares ---------------------------------------------------------------

        private static object InsertParameters(Guid estabelecimentoId, ManualOrder order, StatusPedido status, int pedidoId = 0) => new
        {
            PedidoId = pedidoId,
            order.NomeCliente,
            order.EnderecoEntrega,
            order.TelefoneCliente,
            Status = (int)status,
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
            EstabelecimentoId = estabelecimentoId,
            order.Origem,
            order.OrigemRef,
            order.ConversaId,
            order.Subtotal,
            order.TaxaEntrega,
            order.Desconto
        };

        private static Task InsertItemsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int pedidoId, ManualOrder order)
        {
            var rows = order.Lines.Select((line, index) => new
            {
                PedidoId = pedidoId,
                line.ProdutoId,
                line.Nome,
                line.Quantidade,
                line.PrecoUnitario,
                line.Observacao,
                Adicionais = JsonSerializer.Serialize(line.Adicionais.Select(a => new { id = a.Id, nome = a.Nome, preco = a.Preco }), ItemJson),
                Ordem = index
            }).ToList();

            return connection.ExecuteAsync(@"
INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade, preco_unitario, observacao, adicionais, ordem)
VALUES (@PedidoId, @ProdutoId, @Nome, @Quantidade, @PrecoUnitario, @Observacao, @Adicionais::jsonb, @Ordem);",
                rows, transaction);
        }

        private static async Task<CreatedPedidoDto?> FindByOrigemRefAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid estabelecimentoId, ManualOrder order)
        {
            var row = await connection.QueryFirstOrDefaultAsync<ExistingOrderRow?>(@"
SELECT id AS Id, status_pedido AS StatusPedido, subtotal AS Subtotal, taxa_entrega AS TaxaEntrega
  FROM pedido
 WHERE id_estabelecimento = @EstabelecimentoId AND origem = @Origem AND origem_ref = @OrigemRef
 LIMIT 1;",
                new { EstabelecimentoId = estabelecimentoId, order.Origem, order.OrigemRef }, transaction);
            if (row == null) return null;

            return new CreatedPedidoDto
            {
                Id = row.Id,
                Status = ToStatusKey(StatusPedidoExtensions.FromDbValue(row.StatusPedido) ?? StatusPedido.Pendente),
                Subtotal = row.Subtotal,
                TaxaEntrega = row.TaxaEntrega,
                Total = row.Subtotal.HasValue ? row.Subtotal.Value + (row.TaxaEntrega ?? 0m) : null,
                JaExistia = true
            };
        }

        private static CreatedPedidoDto ToCreatedDto(int pedidoId, StatusPedido status, ManualOrder order, bool jaExistia) => new()
        {
            Id = pedidoId,
            Status = ToStatusKey(status),
            Subtotal = order.Subtotal,
            TaxaEntrega = order.TaxaEntrega,
            Total = order.Value,
            JaExistia = jaExistia,
            Avisos = order.Avisos.ToList()
        };

        private static string ToStatusKey(StatusPedido status) => status.ToApiKey();

        private async Task PublishOrderEventAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int actorUserId, int pedidoId, string action)
        {
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                pedidoId,
                action,
                createdByUserId = actorUserId,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryOrderUpdated,
                estabelecimentoId, motoboyId: null, aggregateVersion: pedidoId, payload, Array.Empty<Guid>());
        }

        private sealed class ExistingOrderRow
        {
            public int Id { get; set; }
            public int? StatusPedido { get; set; }
            public decimal? Subtotal { get; set; }
            public decimal? TaxaEntrega { get; set; }
        }
    }
}
