using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.RepasseIfood;

/// <summary>Repasse do iFood confirmado pelo gerente para um pedido: o que caiu no banco, quando e com qual comprovante.</summary>
public sealed class LiquidarRepasseIfoodRequest
{
    public string? ChaveIdempotencia { get; set; }
    public string? PedidoId { get; set; }
    public Guid ContaDestinoId { get; set; }
    public DateOnly? DataPagamento { get; set; }
    /// <summary>Valor que de fato caiu no banco por este pedido. Diferente do a receber, nada e quitado: fica para conciliar.</summary>
    public decimal? ValorRecebido { get; set; }
    public string? Referencia { get; set; }
    public bool ConfirmacaoHumana { get; set; }
}

public static class SituacaoRepasseIfood
{
    public const string Liquidado = "liquidado", Divergente = "divergente";
}

/// <param name="Situacao">liquidado (o a receber virou recebido no banco) ou divergente (valor nao bate; nada quitado).</param>
/// <param name="SituacaoFinanceira">Status atual do lancamento no livro (a_receber ou recebido).</param>
public sealed record RepasseIfoodResultado(Guid Id, string PedidoId, Guid VendaId, Guid LancamentoId, decimal ValorAReceber,
    decimal ValorRecebido, decimal ValorLiquidado, string Situacao, string SituacaoFinanceira, Guid? PagamentoId, Guid ContaDestinoId,
    DateOnly DataPagamento, string? Referencia, bool Repetido);

public sealed record DivergenciaRepasseDto(Guid Id, decimal ValorRecebido, DateOnly DataPagamento, string? Referencia,
    DateTimeOffset RegistradoEm);

public sealed record RecebivelIfoodDto(string PedidoId, Guid VendaId, Guid LancamentoId, string Descricao, DateTimeOffset DataFato,
    decimal ValorAReceber, IReadOnlyList<DivergenciaRepasseDto> Divergencias);

public interface IRepasseIfoodService
{
    Task<RepasseIfoodResultado> Liquidar(Guid loja, int usuario, LiquidarRepasseIfoodRequest request, CancellationToken ct);
    Task<IReadOnlyList<RecebivelIfoodDto>> AReceber(Guid loja, CancellationToken ct);
}

/// <summary>
/// Liquidacao manual do recebivel iFood (CONTRATOS §9.10). Nao ha fonte oficial de repasse na integracao (so pedidos e eventos),
/// entao o dinheiro so entra quando o gerente confirma. Quita, pelo <see cref="IAcontecimentosService.Liquidar"/>, somente a linha
/// da venda que esta no recebivel iFood; o que o cliente pagou na entrega (dinheiro, maquininha) fica onde esta. Valor diferente
/// do a receber (taxa, parcial) nao quita nem vira despesa: fica registrado como divergencia.
/// </summary>
public sealed class RepasseIfoodService(NpgsqlDataSource fonte, IAcontecimentosService livro) : IRepasseIfoodService
{
    private const string Prefixo = "repasse_ifood:";

    public async Task<RepasseIfoodResultado> Liquidar(Guid loja, int usuario, LiquidarRepasseIfoodRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalido("Envie o repasse.");
        var chave = r.ChaveIdempotencia?.Trim();
        if (string.IsNullOrEmpty(chave) || chave.Length > 160 - Prefixo.Length) throw Invalido("Chave de idempotência obrigatória.");
        var pedido = r.PedidoId?.Trim();
        if (string.IsNullOrEmpty(pedido) || pedido.Length > 120) throw Invalido("Informe o pedido.");
        if (!r.ConfirmacaoHumana) throw Invalido("Confirme que o repasse caiu no banco.");
        if (r.DataPagamento is not { } data) throw Invalido("Informe a data em que o repasse caiu.");
        if (r.ValorRecebido is not { } valor || valor <= 0 || decimal.Round(valor, 2) != valor)
            throw Invalido("Informe o valor que caiu, com até 2 casas.");
        var referencia = string.IsNullOrWhiteSpace(r.Referencia) ? null : r.Referencia.Trim();
        if (referencia?.Length > 120) throw Invalido("Referência com até 120 caracteres.");
        var hash = AcontecimentoRegras.Hash(new { pedido, conta = r.ContaDestinoId, data, valor, referencia, usuario });

        await using var c = await fonte.OpenConnectionAsync(ct);
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));",
                new { k = $"{loja}:{Prefixo}{chave}" }, tx);
            if (await Registro(c, tx, loja, chave) is { } gravado)
            {
                if (gravado.HashConteudo != hash)
                    throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Esta chave já foi usada com outro conteúdo.", 409);
                return await Resultado(c, tx, gravado, true);
            }

            var conta = await c.QuerySingleOrDefaultAsync<(string Tipo, bool Ativo)?>(@"SELECT tipo, ativo FROM gestao_contas
 WHERE estabelecimento_id = @loja AND id = @id;", new { loja, id = r.ContaDestinoId }, tx);
            if (conta is not { Tipo: TipoConta.Banco, Ativo: true })
                throw new GestaoException("CONTA_INVALIDA", "O repasse do iFood cai numa conta de banco ativa desta loja.", 422);

            var venda = await c.QuerySingleOrDefaultAsync<(Guid Id, bool Estornado)?>(@"SELECT a.id, EXISTS (SELECT 1 FROM gestao_acontecimentos e
 WHERE e.estabelecimento_id = a.estabelecimento_id AND e.corrige_id = a.id AND e.correcao = 'estorno') AS estornado
  FROM gestao_acontecimentos a WHERE a.estabelecimento_id = @loja AND a.tipo = 'venda' AND a.origem_tipo = 'pedido'
   AND a.origem_id = @pedido AND a.corrige_id IS NULL;", new { loja, pedido }, tx)
                ?? throw new GestaoException("SEM_VENDA", "Este pedido não tem venda registrada.", 404);
            if (venda.Estornado) throw new GestaoException("JA_ESTORNADO", "A venda deste pedido foi estornada: não há o que receber.", 409);

            var linhas = (await c.QueryAsync<(Guid Id, decimal Liquido, decimal Liquidado)>(@"SELECT s.id, s.valor + coalesce((SELECT sum(k.valor)
 FROM gestao_lancamentos k WHERE k.estabelecimento_id = s.estabelecimento_id AND k.corrige_lancamento_id = s.id), 0), s.liquidado
  FROM gestao_lancamentos_situacao s JOIN gestao_contas g ON g.estabelecimento_id = s.estabelecimento_id AND g.id = s.conta_id
 WHERE s.estabelecimento_id = @loja AND s.acontecimento_id = @venda AND s.corrige_lancamento_id IS NULL
   AND s.natureza = 'receita' AND s.status_registro = 'a_receber' AND g.tipo = 'recebivel' AND g.subtipo = 'ifood'
 ORDER BY s.linha;", new { loja, venda = venda.Id }, tx)).ToList();
            if (linhas.Count == 0)
                throw new GestaoException("SEM_RECEBIVEL", "A venda deste pedido não tem valor a receber do iFood.", 404);
            if (linhas.Count > 1)
                throw new GestaoException("CONCILIAR", "A venda tem mais de um valor a receber do iFood: concilie pelo Financeiro.", 409);
            var (lancamento, aReceber, jaLiquidado) = linhas[0];

            if (valor != aReceber)
            {
                if (jaLiquidado != 0) throw new GestaoException("JA_LIQUIDADO", "O repasse deste pedido já foi recebido.", 409);
                // Taxa descontada, pagamento parcial ou a mais: nada se quita sem decisao; fica para conciliar.
                var id = Guid.NewGuid();
                await c.ExecuteAsync(@"INSERT INTO gestao_repasses_ifood (id, estabelecimento_id, chave_idempotencia, hash_conteudo, pedido_id,
 venda_id, lancamento_id, conta_destino_id, data_pagamento, valor_a_receber, valor_recebido, situacao, referencia, responsavel_id)
VALUES (@id, @loja, @chave, @hash, @pedido, @venda, @lancamento, @conta, @data, @aReceber, @valor, 'divergente', @referencia, @usuario);",
                    new { id, loja, chave, hash, pedido, venda = venda.Id, lancamento, conta = r.ContaDestinoId, data, aReceber, valor, referencia, usuario }, tx);
                var divergente = (await Registro(c, tx, loja, chave))!;
                var resultado = await Resultado(c, tx, divergente, false);
                await tx.CommitAsync(ct);
                return resultado;
            }
            await tx.CommitAsync(ct);

            // O livro trava o lancamento e a chave: replay devolve o mesmo pagamento; outra chave sobre o ja recebido e recusada.
            AcontecimentoPublicado pagamento;
            try
            {
                pagamento = await livro.Liquidar(loja, usuario, new LiquidarRequest
                {
                    ChaveIdempotencia = Prefixo + chave, LancamentoId = lancamento, ContaId = r.ContaDestinoId, DataPagamento = data,
                    Descricao = $"Repasse iFood do pedido #{pedido}" + (referencia is null ? "" : $" ({referencia})"),
                }, ct);
            }
            catch (GestaoException ex) when (ex.Codigo == "DUPLICADO")
            {
                throw new GestaoException("JA_LIQUIDADO", "O repasse deste pedido já foi recebido.", 409);
            }

            await using var tx2 = await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));", new { k = $"{loja}:{Prefixo}{chave}" }, tx2);
            var repetido = await Registro(c, tx2, loja, chave) is not null;
            if (!repetido)
                await c.ExecuteAsync(@"INSERT INTO gestao_repasses_ifood (id, estabelecimento_id, chave_idempotencia, hash_conteudo, pedido_id,
 venda_id, lancamento_id, conta_destino_id, data_pagamento, valor_a_receber, valor_recebido, situacao, pagamento_id, referencia, responsavel_id)
VALUES (@id, @loja, @chave, @hash, @pedido, @venda, @lancamento, @conta, @data, @aReceber, @valor, 'liquidado', @pagamento, @referencia, @usuario);",
                    new { id = Guid.NewGuid(), loja, chave, hash, pedido, venda = venda.Id, lancamento, conta = r.ContaDestinoId, data, aReceber,
                        valor, pagamento = pagamento.Id, referencia, usuario }, tx2);
            var liquidado = (await Registro(c, tx2, loja, chave))!;
            var final = await Resultado(c, tx2, liquidado, pagamento.Repetido || repetido);
            await tx2.CommitAsync(ct);
            return final;
        }
    }

    public async Task<IReadOnlyList<RecebivelIfoodDto>> AReceber(Guid loja, CancellationToken ct)
    {
        await using var c = await fonte.OpenConnectionAsync(ct);
        var abertos = (await c.QueryAsync<(string PedidoId, Guid VendaId, Guid LancamentoId, string Descricao, DateTimeOffset DataFato, decimal Valor)>(
            @"SELECT a.origem_id, a.id, s.id, a.descricao, a.data_fato, s.valor + coalesce((SELECT sum(k.valor) FROM gestao_lancamentos k
 WHERE k.estabelecimento_id = s.estabelecimento_id AND k.corrige_lancamento_id = s.id), 0)
  FROM gestao_lancamentos_situacao s
  JOIN gestao_acontecimentos a ON a.estabelecimento_id = s.estabelecimento_id AND a.id = s.acontecimento_id
  JOIN gestao_contas g ON g.estabelecimento_id = s.estabelecimento_id AND g.id = s.conta_id
 WHERE s.estabelecimento_id = @loja AND s.corrige_lancamento_id IS NULL AND s.natureza = 'receita' AND s.status = 'a_receber'
   AND g.tipo = 'recebivel' AND g.subtipo = 'ifood' AND a.tipo = 'venda' AND a.origem_tipo = 'pedido'
   AND NOT EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = a.estabelecimento_id AND e.corrige_id = a.id
                   AND e.correcao = 'estorno')
 ORDER BY a.data_fato, a.id;", new { loja })).ToList();
        var divergencias = (await c.QueryAsync<(Guid LancamentoId, Guid Id, decimal ValorRecebido, DateOnly DataPagamento, string? Referencia,
            DateTimeOffset RegistradoEm)>(@"SELECT lancamento_id, id, valor_recebido, data_pagamento, referencia, registrado_em
  FROM gestao_repasses_ifood WHERE estabelecimento_id = @loja AND situacao = 'divergente' ORDER BY registrado_em;", new { loja }))
            .ToLookup(d => d.LancamentoId, d => new DivergenciaRepasseDto(d.Id, d.ValorRecebido, d.DataPagamento, d.Referencia, d.RegistradoEm));
        return abertos.Where(a => a.Valor > 0).Select(a => new RecebivelIfoodDto(a.PedidoId, a.VendaId, a.LancamentoId, a.Descricao,
            a.DataFato, a.Valor, divergencias[a.LancamentoId].ToList())).ToList();
    }

    private sealed class RegistroRow
    {
        public Guid Id { get; set; }
        public string HashConteudo { get; set; } = "";
        public string PedidoId { get; set; } = "";
        public Guid VendaId { get; set; }
        public Guid LancamentoId { get; set; }
        public Guid ContaDestinoId { get; set; }
        public DateOnly DataPagamento { get; set; }
        public decimal ValorAReceber { get; set; }
        public decimal ValorRecebido { get; set; }
        public string Situacao { get; set; } = "";
        public Guid? PagamentoId { get; set; }
        public string? Referencia { get; set; }
    }

    private static Task<RegistroRow?> Registro(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, string chave) =>
        c.QuerySingleOrDefaultAsync<RegistroRow?>(@"SELECT id, hash_conteudo AS HashConteudo, pedido_id AS PedidoId, venda_id AS VendaId,
 lancamento_id AS LancamentoId, conta_destino_id AS ContaDestinoId, data_pagamento AS DataPagamento, valor_a_receber AS ValorAReceber,
 valor_recebido AS ValorRecebido, situacao, pagamento_id AS PagamentoId, referencia
  FROM gestao_repasses_ifood WHERE estabelecimento_id = @loja AND chave_idempotencia = @chave;", new { loja, chave }, tx);

    private static async Task<RepasseIfoodResultado> Resultado(NpgsqlConnection c, NpgsqlTransaction tx, RegistroRow g, bool repetido)
    {
        var status = await c.ExecuteScalarAsync<string>("SELECT status FROM gestao_lancamentos_situacao WHERE id = @id;",
            new { id = g.LancamentoId }, tx) ?? StatusLancamento.AReceber;
        var valorLiquidado = g.PagamentoId is { } pagamento
            ? await c.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(valor), 0) FROM gestao_liquidacoes WHERE acontecimento_id = @pagamento;",
                new { pagamento }, tx)
            : 0m;
        return new RepasseIfoodResultado(g.Id, g.PedidoId, g.VendaId, g.LancamentoId, g.ValorAReceber, g.ValorRecebido, valorLiquidado,
            g.Situacao, status, g.PagamentoId, g.ContaDestinoId, g.DataPagamento, g.Referencia, repetido);
    }

    private static GestaoException Invalido(string mensagem) => new("CAMPO_INVALIDO", mensagem, 422);
}
