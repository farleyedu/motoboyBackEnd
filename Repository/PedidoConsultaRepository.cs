using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Model.Enum;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class PedidoConsultaRepository : IPedidoConsultaRepository
    {
        // Colunas legadas de pedido podem ser text/numeric/time/timestamp: tudo e lido como texto e
        // convertido com seguranca em C#. Valor mal formatado vira null naquele campo, nunca 500.
        private const string Numeric = @"'^-?[0-9]+(\.[0-9]+)?$'";
        private readonly NpgsqlDataSource _dataSource;

        public PedidoConsultaRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public async Task<PedidoListaDto> ListAsync(Guid estabelecimentoId, PedidoFiltro filtro)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var core = await PedidoColumnTypes.HasCoreSchemaAsync(connection, null);

            var origemExpr = core ? "p.origem" : "CASE WHEN p.id_ifood IS NOT NULL THEN 'ifood' ELSE 'atendente' END";
            var (where, parameters) = BuildWhere(estabelecimentoId, filtro, origemExpr);

            var total = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM pedido p WHERE {where};", parameters);

            parameters.Add("Limit", filtro.PageSize);
            parameters.Add("Offset", filtro.Offset);
            var rows = (await connection.QueryAsync<ResumoRow>($@"
SELECT p.id AS Id,
       COALESCE(p.status_pedido, 1) AS StatusPedido,
       {origemExpr}::text AS Origem,
       p.nome_cliente::text AS NomeCliente,
       p.telefone_cliente::text AS TelefoneCliente,
       COALESCE(p.entrega_bairro, p.region)::text AS Bairro,
       p.endereco_entrega::text AS EnderecoEntrega,
       CASE WHEN p.value::text ~ {Numeric} THEN p.value::text::NUMERIC END AS Total,
       {(core ? "p.subtotal" : "NULL::NUMERIC")} AS Subtotal,
       {(core ? "p.taxa_entrega" : "NULL::NUMERIC")} AS TaxaEntrega,
       p.tipo_pagamento::text AS FormaPagamento,
       p.data_pedido::text AS DataPedidoRaw,
       p.motoboy_responsavel AS MotoboyId,
       m.nome::text AS MotoboyNome,
       p.items::text AS Items,
       {(core ? "p.conversa_id" : "NULL::UUID")} AS ConversaId
  FROM pedido p
  LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE {where}
 ORDER BY p.id DESC
 LIMIT @Limit OFFSET @Offset;", parameters)).ToList();

            return new PedidoListaDto
            {
                Itens = rows.Select(ToResumo).ToList(),
                Total = total,
                Page = filtro.Page,
                PageSize = filtro.PageSize
            };
        }

        public async Task<PedidoDetalheDto?> GetAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var core = await PedidoColumnTypes.HasCoreSchemaAsync(connection, null);
            var origemExpr = core ? "p.origem" : "CASE WHEN p.id_ifood IS NOT NULL THEN 'ifood' ELSE 'atendente' END";

            var row = await connection.QueryFirstOrDefaultAsync<DetalheRow>($@"
SELECT p.id AS Id,
       COALESCE(p.status_pedido, 1) AS StatusPedido,
       {origemExpr}::text AS Origem,
       p.nome_cliente::text AS NomeCliente,
       p.telefone_cliente::text AS TelefoneCliente,
       COALESCE(p.entrega_bairro, p.region)::text AS Bairro,
       p.endereco_entrega::text AS EnderecoEntrega,
       CASE WHEN p.value::text ~ {Numeric} THEN p.value::text::NUMERIC END AS Total,
       {(core ? "p.subtotal" : "NULL::NUMERIC")} AS Subtotal,
       {(core ? "p.taxa_entrega" : "NULL::NUMERIC")} AS TaxaEntrega,
       p.tipo_pagamento::text AS FormaPagamento,
       p.data_pedido::text AS DataPedidoRaw,
       p.motoboy_responsavel AS MotoboyId,
       m.nome::text AS MotoboyNome,
       p.items::text AS Items,
       {(core ? "p.conversa_id" : "NULL::UUID")} AS ConversaId,
       p.entrega_rua::text AS Rua,
       p.entrega_numero::text AS Numero,
       p.entrega_cidade::text AS Cidade,
       p.entrega_estado::text AS Estado,
       p.entrega_cep::text AS Cep,
       CASE WHEN p.latitude::text ~ {Numeric} THEN p.latitude::text::DOUBLE PRECISION END AS Latitude,
       CASE WHEN p.longitude::text ~ {Numeric} THEN p.longitude::text::DOUBLE PRECISION END AS Longitude,
       p.observacoes::text AS Observacoes,
       CASE WHEN p.troco::text ~ {Numeric} THEN p.troco::text::NUMERIC END AS Troco,
       p.status_pagamento::text AS StatusPagamento,
       p.previsao_entrega::text AS PrevisaoEntregaRaw,
       p.codigo_entrega::text AS CodigoEntrega
  FROM pedido p
  LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE p.id = @PedidoId AND p.id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId });
            if (row == null) return null;

            var itens = new List<PedidoItemDto>();
            if (core)
            {
                var structured = await connection.QueryAsync<ItemRow>(@"
SELECT nome AS Nome, quantidade AS Quantidade, preco_unitario AS PrecoUnitario,
       observacao AS Observacao, adicionais::text AS Adicionais
  FROM pedido_item WHERE pedido_id = @PedidoId ORDER BY ordem, id;", new { PedidoId = pedidoId });
                itens = structured.Select(ToItem).ToList();
            }
            if (itens.Count == 0) itens = LegacyItemsParser.Parse(row.Items);

            var anchor = DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw);
            var detail = new PedidoDetalheDto
            {
                Rua = row.Rua,
                Numero = row.Numero,
                Cidade = row.Cidade,
                Estado = row.Estado,
                Cep = row.Cep,
                Latitude = row.Latitude,
                Longitude = row.Longitude,
                Observacoes = row.Observacoes,
                Troco = row.Troco,
                StatusPagamento = row.StatusPagamento,
                PrevisaoEntrega = DeliveryRules.ParseStoredDateTime(row.PrevisaoEntregaRaw, anchor),
                CodigoEntrega = row.CodigoEntrega,
                Itens = itens
            };
            Fill(detail, row);
            return detail;
        }

        private static (string Where, DynamicParameters Parameters) BuildWhere(Guid estabelecimentoId, PedidoFiltro filtro, string origemExpr)
        {
            var clauses = new List<string> { "p.id_estabelecimento = @EstabelecimentoId" };
            var parameters = new DynamicParameters();
            parameters.Add("EstabelecimentoId", estabelecimentoId);

            if (filtro.Status.Length > 0)
            {
                clauses.Add("COALESCE(p.status_pedido, 1) = ANY(@Status)");
                parameters.Add("Status", filtro.Status);
            }
            if (filtro.Origem.Length > 0)
            {
                clauses.Add($"{origemExpr} = ANY(@Origem)");
                parameters.Add("Origem", filtro.Origem);
            }
            // Comparacao textual em AAAA-MM-DD: vale para as colunas date/timestamp; datas legadas em
            // texto dd/MM/AAAA nao casam com o periodo (ficam de fora quando ele e informado).
            if (filtro.De != null)
            {
                clauses.Add("LEFT(p.data_pedido::text, 10) >= @De");
                parameters.Add("De", filtro.De);
            }
            if (filtro.Ate != null)
            {
                clauses.Add("LEFT(p.data_pedido::text, 10) <= @Ate");
                parameters.Add("Ate", filtro.Ate);
            }
            if (filtro.BuscaLike != null)
            {
                var text = "(p.nome_cliente::text ILIKE @Like OR p.endereco_entrega::text ILIKE @Like OR p.region::text ILIKE @Like OR p.telefone_cliente::text ILIKE @Like)";
                if (filtro.BuscaDigitos != null)
                {
                    var byNumber = new List<string> { "p.telefone_cliente::text LIKE '%' || @Digitos || '%'" };
                    if (filtro.BuscaId.HasValue)
                    {
                        byNumber.Add("p.id = @BuscaId");
                        parameters.Add("BuscaId", filtro.BuscaId.Value);
                    }
                    parameters.Add("Digitos", filtro.BuscaDigitos);
                    clauses.Add("(" + string.Join(" OR ", byNumber) + ")");
                }
                else
                {
                    clauses.Add(text);
                }
                parameters.Add("Like", filtro.BuscaLike);
            }

            return (string.Join(" AND ", clauses), parameters);
        }

        private static PedidoResumoDto ToResumo(ResumoRow row)
        {
            var dto = new PedidoResumoDto();
            Fill(dto, row);
            return dto;
        }

        private static void Fill(PedidoResumoDto dto, ResumoRow row)
        {
            dto.Id = row.Id;
            dto.Status = StatusPedidoExtensions.ToApiKey(row.StatusPedido);
            dto.Origem = row.Origem ?? "atendente";
            dto.NomeCliente = row.NomeCliente;
            dto.TelefoneCliente = row.TelefoneCliente;
            dto.Bairro = row.Bairro;
            dto.EnderecoEntrega = row.EnderecoEntrega;
            dto.Total = row.Total;
            dto.Subtotal = row.Subtotal;
            dto.TaxaEntrega = row.TaxaEntrega;
            dto.FormaPagamento = row.FormaPagamento;
            dto.CriadoEm = DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw);
            dto.MotoboyId = row.MotoboyId;
            dto.MotoboyNome = row.MotoboyNome;
            dto.QuantidadeItens = LegacyItemsParser.CountUnits(row.Items);
            dto.ConversaId = row.ConversaId;
        }

        private static PedidoItemDto ToItem(ItemRow row)
        {
            var addons = new List<PedidoAdicionalDto>();
            if (!string.IsNullOrWhiteSpace(row.Adicionais))
            {
                try
                {
                    using var doc = JsonDocument.Parse(row.Adicionais);
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        var nome = element.TryGetProperty("nome", out var n) ? n.GetString() : null;
                        var preco = element.TryGetProperty("preco", out var p) && p.TryGetDecimal(out var d) ? d : 0m;
                        if (!string.IsNullOrWhiteSpace(nome)) addons.Add(new PedidoAdicionalDto { Nome = nome!, Preco = preco });
                    }
                }
                catch (JsonException)
                {
                    // adicionais ilegiveis: o item aparece sem eles em vez de derrubar o detalhe
                }
            }
            return new PedidoItemDto
            {
                Nome = row.Nome,
                Quantidade = row.Quantidade,
                PrecoUnitario = row.PrecoUnitario,
                Observacao = row.Observacao,
                Adicionais = addons,
                Total = decimal.Round(row.Quantidade * (row.PrecoUnitario + addons.Sum(a => a.Preco)), 2)
            };
        }

        private class ResumoRow
        {
            public int Id { get; set; }
            public int StatusPedido { get; set; }
            public string? Origem { get; set; }
            public string? NomeCliente { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? Bairro { get; set; }
            public string? EnderecoEntrega { get; set; }
            public decimal? Total { get; set; }
            public decimal? Subtotal { get; set; }
            public decimal? TaxaEntrega { get; set; }
            public string? FormaPagamento { get; set; }
            public string? DataPedidoRaw { get; set; }
            public int? MotoboyId { get; set; }
            public string? MotoboyNome { get; set; }
            public string? Items { get; set; }
            public Guid? ConversaId { get; set; }
        }

        private sealed class DetalheRow : ResumoRow
        {
            public string? Rua { get; set; }
            public string? Numero { get; set; }
            public string? Cidade { get; set; }
            public string? Estado { get; set; }
            public string? Cep { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Observacoes { get; set; }
            public decimal? Troco { get; set; }
            public string? StatusPagamento { get; set; }
            public string? PrevisaoEntregaRaw { get; set; }
            public string? CodigoEntrega { get; set; }
        }

        private sealed class ItemRow
        {
            public string Nome { get; set; } = string.Empty;
            public int Quantidade { get; set; }
            public decimal PrecoUnitario { get; set; }
            public string? Observacao { get; set; }
            public string? Adicionais { get; set; }
        }
    }
}
