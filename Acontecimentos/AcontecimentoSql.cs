using System.Text.Json;
using Dapper;
using Npgsql;

namespace APIBack.Acontecimentos;

/// <summary>SQL do livro de acontecimentos (Dapper). So insere e le: o banco recusa UPDATE/DELETE nessas tabelas.</summary>
internal static class AcontecimentoSql
{
    private sealed class AcontecimentoRow
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = "";
        public string Modulo { get; set; } = "";
        public DateTimeOffset DataFato { get; set; }
        public DateTimeOffset RegistradoEm { get; set; }
        public DateOnly MesReferencia { get; set; }
        public int? ResponsavelId { get; set; }
        public string? ResponsavelNome { get; set; }
        public string? OrigemTipo { get; set; }
        public string? OrigemId { get; set; }
        public string? DocumentoChave { get; set; }
        public string Descricao { get; set; } = "";
        public string Dados { get; set; } = "{}";
        public Guid? CorrigeId { get; set; }
        public string? Correcao { get; set; }
        public string? Motivo { get; set; }
        public decimal? ValorAnterior { get; set; }
        public decimal? ValorNovo { get; set; }
        public string HashConteudo { get; set; } = "";
    }

    private sealed class LancamentoRow
    {
        public Guid Id { get; set; }
        public Guid AcontecimentoId { get; set; }
        public short Linha { get; set; }
        public Guid? ContaId { get; set; }
        public string Natureza { get; set; } = "";
        public string Sentido { get; set; } = "";
        public decimal Valor { get; set; }
        public string Status { get; set; } = "";
        public string StatusRegistro { get; set; } = "";
        public DateOnly MesReferencia { get; set; }
        public DateOnly? DataVencimento { get; set; }
        public DateOnly? DataPagamento { get; set; }
        public short Parcela { get; set; }
        public short Parcelas { get; set; }
        public DateOnly? FaturaMes { get; set; }
        public string? Categoria { get; set; }
        public string? Finalidade { get; set; }
        public string? Descricao { get; set; }
        public Guid? CorrigeLancamentoId { get; set; }
        public decimal Liquidado { get; set; }
    }

    private sealed class MovimentoRow
    {
        public Guid Id { get; set; }
        public Guid AcontecimentoId { get; set; }
        public short Linha { get; set; }
        public Guid ItemId { get; set; }
        public string ItemNome { get; set; } = "";
        public decimal Quantidade { get; set; }
        public string Unidade { get; set; } = "";
        public string Tipo { get; set; } = "";
        public string? Motivo { get; set; }
        public DateOnly MesReferencia { get; set; }
        public int ConfirmadoPor { get; set; }
        public DateTimeOffset ConfirmadoEm { get; set; }
        public Guid? CorrigeMovimentoId { get; set; }
    }

    private sealed class LiquidacaoRow
    {
        public Guid Id { get; set; }
        public Guid AcontecimentoId { get; set; }
        public Guid LancamentoId { get; set; }
        public Guid ContaId { get; set; }
        public decimal Valor { get; set; }
        public DateOnly DataPagamento { get; set; }
        public Guid? CorrigeLiquidacaoId { get; set; }
    }

    private sealed class ContaRow
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = "";
        public string? Subtipo { get; set; }
        public string Nome { get; set; } = "";
        public short? DiaFechamento { get; set; }
        public short? DiaVencimento { get; set; }
        public bool Ativo { get; set; }
        public decimal Saldo { get; set; }
    }

    private const string LancamentoColunas = @"s.id, s.acontecimento_id, s.linha, s.conta_id, s.natureza, s.sentido, s.valor, s.status,
        s.status_registro, s.mes_referencia, s.data_vencimento, s.data_pagamento, s.parcela, s.parcelas, s.fatura_mes,
        s.categoria, s.finalidade, s.descricao, s.corrige_lancamento_id, s.liquidado";

    // ------------------------------------------------------------------ escrita

    public static Task TravarChave(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, string chave) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@K, 0));", new { K = $"gestao:{loja}:{chave}" }, tx);

    public static Task TravarAlvo(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@K, 0));", new { K = $"gestao-alvo:{loja}:{id}" }, tx);

    public static Task<(Guid Id, string Hash)?> PorChave(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, string chave) =>
        c.QuerySingleOrDefaultAsync<(Guid, string)?>(
            "SELECT id, hash_conteudo FROM gestao_acontecimentos WHERE estabelecimento_id = @loja AND chave_idempotencia = @chave;",
            new { loja, chave }, tx);

    public static async Task<string?> Fuso(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja) =>
        await c.ExecuteScalarAsync<string?>("SELECT timezone_iana FROM estabelecimentos WHERE id = @loja;", new { loja }, tx);

    public static async Task<Dictionary<Guid, ContaInfo>> ContasInfo(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja)
    {
        var rows = await c.QueryAsync<ContaRow>(
            "SELECT id, tipo, subtipo, nome, dia_fechamento, dia_vencimento, ativo FROM gestao_contas WHERE estabelecimento_id = @loja;",
            new { loja }, tx);
        return rows.ToDictionary(r => r.Id, r => new ContaInfo(r.Id, r.Tipo, r.DiaFechamento, r.DiaVencimento, r.Ativo));
    }

    public static async Task<HashSet<DateOnly>> MesesFechados(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja) =>
        (await c.QueryAsync<DateOnly>("SELECT mes FROM gestao_meses_fechados WHERE estabelecimento_id = @loja ORDER BY mes;",
            new { loja }, tx)).ToHashSet();

    public static async Task<Guid> Inserir(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, AcontecimentoPlano p)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO gestao_acontecimentos (id, estabelecimento_id, tipo, modulo, data_fato, mes_referencia, responsavel_id, origem_tipo,
    origem_id, documento_chave, descricao, dados, corrige_id, correcao, motivo, valor_anterior, valor_novo, chave_idempotencia,
    hash_conteudo)
VALUES (@id, @loja, @Tipo, @Modulo, @DataFato, @MesReferencia, @ResponsavelId, @OrigemTipo, @OrigemId, @DocumentoChave, @Descricao,
    CAST(@DadosJson AS jsonb), @CorrigeId, @Correcao, @Motivo, @ValorAnterior, @ValorNovo, @Chave, @Hash);",
            new
            {
                id, loja, p.Tipo, p.Modulo, DataFato = p.DataFato.ToUniversalTime(), p.MesReferencia, p.ResponsavelId, p.OrigemTipo, p.OrigemId, p.DocumentoChave,
                p.Descricao, p.DadosJson, p.CorrigeId, p.Correcao, p.Motivo, p.ValorAnterior, p.ValorNovo, p.Chave, p.Hash
            }, tx);
        foreach (var l in p.Lancamentos)
            await c.ExecuteAsync(@"
INSERT INTO gestao_lancamentos (id, estabelecimento_id, acontecimento_id, linha, conta_id, natureza, sentido, valor, status,
    mes_referencia, data_vencimento, data_pagamento, parcela, parcelas, fatura_mes, categoria, finalidade, descricao,
    corrige_lancamento_id)
VALUES (@Id, @loja, @AcontecimentoId, @Linha, @ContaId, @Natureza, @Sentido, @Valor, @Status, @MesReferencia, @DataVencimento, @DataPagamento,
    @Parcela, @Parcelas, @FaturaMes, @Categoria, @Finalidade, @Descricao, @CorrigeLancamentoId);",
                new
                {
                    Id = Guid.NewGuid(), loja, AcontecimentoId = id, Linha = (short)l.Linha, l.ContaId, l.Natureza, l.Sentido, l.Valor, l.Status,
                    l.MesReferencia, l.DataVencimento, l.DataPagamento, Parcela = (short)l.Parcela, Parcelas = (short)l.Parcelas,
                    l.FaturaMes, l.Categoria, l.Finalidade, l.Descricao, l.CorrigeLancamentoId
                }, tx);
        foreach (var m in p.Movimentos)
            await c.ExecuteAsync(@"
INSERT INTO gestao_movimentos_estoque (id, estabelecimento_id, acontecimento_id, linha, item_id, item_nome, quantidade, unidade,
    tipo, motivo, mes_referencia, confirmado_por, confirmado_em, corrige_movimento_id)
VALUES (@Id, @loja, @AcontecimentoId, @Linha, @ItemId, @ItemNome, @Quantidade, @Unidade, @Tipo, @Motivo, @MesReferencia, @ConfirmadoPor, now(),
    @CorrigeMovimentoId);",
                new
                {
                    Id = Guid.NewGuid(), loja, AcontecimentoId = id, Linha = (short)m.Linha, m.ItemId, m.ItemNome, m.Quantidade, m.Unidade, m.Tipo,
                    m.Motivo, m.MesReferencia, ConfirmadoPor = p.ResponsavelId, m.CorrigeMovimentoId
                }, tx);
        foreach (var q in p.Liquidacoes)
            await c.ExecuteAsync(@"
INSERT INTO gestao_liquidacoes (id, estabelecimento_id, acontecimento_id, lancamento_id, conta_id, valor, data_pagamento,
    corrige_liquidacao_id)
VALUES (@Id, @loja, @AcontecimentoId, @LancamentoId, @ContaId, @Valor, @DataPagamento, @CorrigeLiquidacaoId);",
                new { Id = Guid.NewGuid(), loja, AcontecimentoId = id, q.LancamentoId, q.ContaId, q.Valor, q.DataPagamento, q.CorrigeLiquidacaoId }, tx);
        return id;
    }

    public static Task RegistrarFechamento(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, DateOnly mes, Guid acontecimento,
        int responsavel) =>
        c.ExecuteAsync(@"INSERT INTO gestao_meses_fechados (estabelecimento_id, mes, acontecimento_id, fechado_por)
VALUES (@loja, @mes, @acontecimento, @responsavel);", new { loja, mes, acontecimento, responsavel }, tx);

    public static async Task<Guid> InserirConta(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int? responsavel,
        string chave, string tipo, string? subtipo, string nome, int? fechamento, int? vencimento)
    {
        var existente = await c.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM gestao_contas WHERE estabelecimento_id = @loja AND chave_idempotencia = @chave;", new { loja, chave }, tx);
        if (existente is { } id) return id;
        id = Guid.NewGuid();
        await c.ExecuteAsync(@"INSERT INTO gestao_contas (id, estabelecimento_id, tipo, subtipo, nome, dia_fechamento, dia_vencimento,
    chave_idempotencia, criado_por) VALUES (@id, @loja, @tipo, @subtipo, @nome, @fechamento, @vencimento, @chave, @responsavel);",
            new { id, loja, tipo, subtipo, nome, fechamento = (short?)fechamento, vencimento = (short?)vencimento, chave, responsavel }, tx);
        return id;
    }

    // ------------------------------------------------------------------ estado para correcoes

    public static async Task<AcontecimentoAtual?> Atual(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid id) =>
        (await c.QueryAsync<(Guid, string, string, string?, DateOnly, bool)>(@"
SELECT a.id, a.tipo, a.modulo, a.correcao, a.mes_referencia,
       EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = a.estabelecimento_id
               AND e.corrige_id = a.id AND e.correcao = 'estorno')
  FROM gestao_acontecimentos a WHERE a.estabelecimento_id = @loja AND a.id = @id;", new { loja, id }, tx))
        .Select(r => new AcontecimentoAtual(r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6)).SingleOrDefault();

    /// <summary>Lancamentos do acontecimento e todas as correcoes que apontam para eles.</summary>
    public static async Task<List<LancamentoAtual>> LancamentosAtuais(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja,
        Guid acontecimento) =>
        (await c.QueryAsync<LancamentoRow>($@"
SELECT {LancamentoColunas} FROM gestao_lancamentos_situacao s
 WHERE s.estabelecimento_id = @loja AND (s.acontecimento_id = @acontecimento OR s.corrige_lancamento_id IN
       (SELECT o.id FROM gestao_lancamentos o WHERE o.estabelecimento_id = @loja AND o.acontecimento_id = @acontecimento));",
            new { loja, acontecimento }, tx)).Select(Atual).ToList();

    public static async Task<List<LancamentoAtual>> LancamentoECorrecoes(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja,
        Guid lancamento) =>
        (await c.QueryAsync<LancamentoRow>($@"
SELECT {LancamentoColunas} FROM gestao_lancamentos_situacao s
 WHERE s.estabelecimento_id = @loja AND (s.id = @lancamento OR s.corrige_lancamento_id = @lancamento);",
            new { loja, lancamento }, tx)).Select(Atual).ToList();

    public static async Task<List<MovimentoAtual>> MovimentosAtuais(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja,
        Guid? acontecimento, Guid? movimento) =>
        (await c.QueryAsync<MovimentoRow>(@"
SELECT m.* FROM gestao_movimentos_estoque m
 WHERE m.estabelecimento_id = @loja AND (
       (@acontecimento::uuid IS NOT NULL AND (m.acontecimento_id = @acontecimento OR m.corrige_movimento_id IN
           (SELECT o.id FROM gestao_movimentos_estoque o WHERE o.estabelecimento_id = @loja AND o.acontecimento_id = @acontecimento)))
    OR (@movimento::uuid IS NOT NULL AND (m.id = @movimento OR m.corrige_movimento_id = @movimento)));",
            new { loja, acontecimento, movimento }, tx))
        .Select(m => new MovimentoAtual(m.Id, m.AcontecimentoId, m.Linha, m.ItemId, m.ItemNome, m.Quantidade, m.Unidade, m.Tipo,
            m.MesReferencia, m.CorrigeMovimentoId)).ToList();

    public static async Task<List<LiquidacaoAtual>> LiquidacoesAtuais(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja,
        Guid acontecimento) =>
        (await c.QueryAsync<LiquidacaoRow>(@"
SELECT q.* FROM gestao_liquidacoes q
 WHERE q.estabelecimento_id = @loja AND (q.acontecimento_id = @acontecimento OR q.corrige_liquidacao_id IN
       (SELECT o.id FROM gestao_liquidacoes o WHERE o.estabelecimento_id = @loja AND o.acontecimento_id = @acontecimento));",
            new { loja, acontecimento }, tx))
        .Select(q => new LiquidacaoAtual(q.Id, q.AcontecimentoId, q.LancamentoId, q.ContaId, q.Valor, q.DataPagamento,
            q.CorrigeLiquidacaoId)).ToList();

    private static LancamentoAtual Atual(LancamentoRow r) => new(r.Id, r.AcontecimentoId, r.Linha, r.ContaId, r.Natureza,
        r.Sentido, r.Valor, r.StatusRegistro, r.Status, r.MesReferencia, r.DataVencimento, r.Parcela, r.Parcelas, r.FaturaMes,
        r.Categoria, r.Finalidade, r.Descricao, r.CorrigeLancamentoId, r.Liquidado);

    // ------------------------------------------------------------------ leitura

    public static async Task<List<AcontecimentoDto>> Acontecimentos(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja,
        IReadOnlyCollection<Guid>? ids, PeriodoFiltro? f, string? fuso)
    {
        var rows = (await c.QueryAsync<AcontecimentoRow>(@"
SELECT a.id, a.tipo, a.modulo, a.data_fato, a.registrado_em, a.mes_referencia, a.responsavel_id, u.nome::text AS responsavel_nome,
       a.origem_tipo, a.origem_id, a.documento_chave, a.descricao, a.dados::text AS dados, a.corrige_id, a.correcao, a.motivo,
       a.valor_anterior, a.valor_novo, a.hash_conteudo
  FROM gestao_acontecimentos a
  LEFT JOIN usuario u ON u.id = a.responsavel_id
 WHERE a.estabelecimento_id = @loja
   AND (@ids::uuid[] IS NULL OR a.id = ANY(@ids))
   AND (@mes::date IS NULL OR a.mes_referencia = @mes
        OR EXISTS (SELECT 1 FROM gestao_lancamentos l WHERE l.estabelecimento_id = a.estabelecimento_id
                   AND l.acontecimento_id = a.id AND l.mes_referencia = @mes))
   AND (@dia::date IS NULL OR (a.data_fato AT TIME ZONE @fuso)::date = @dia)
   AND (@modulo::text IS NULL OR a.modulo = @modulo)
   AND (@tipo::text IS NULL OR a.tipo = @tipo)
   AND (@origemTipo::text IS NULL OR a.origem_tipo = @origemTipo)
   AND (@origemId::text IS NULL OR a.origem_id = @origemId)
 ORDER BY a.data_fato DESC, a.registrado_em DESC
 LIMIT @limite;",
            new
            {
                loja, ids = ids?.ToArray(), mes = f?.Mes, dia = f?.Dia, fuso = fuso ?? "America/Sao_Paulo", modulo = f?.Modulo,
                tipo = f?.Tipo, origemTipo = f?.OrigemTipo, origemId = f?.OrigemId, limite = Math.Clamp(f?.Limite ?? 200, 1, 500)
            }, tx)).ToList();
        if (rows.Count == 0) return new();
        var chaves = rows.Select(r => r.Id).ToArray();
        var lancs = (await c.QueryAsync<LancamentoRow>($@"SELECT {LancamentoColunas} FROM gestao_lancamentos_situacao s
 WHERE s.estabelecimento_id = @loja AND s.acontecimento_id = ANY(@chaves) ORDER BY s.linha;", new { loja, chaves }, tx))
            .ToLookup(l => l.AcontecimentoId);
        var movs = (await c.QueryAsync<MovimentoRow>(@"SELECT * FROM gestao_movimentos_estoque
 WHERE estabelecimento_id = @loja AND acontecimento_id = ANY(@chaves) ORDER BY linha;", new { loja, chaves }, tx))
            .ToLookup(m => m.AcontecimentoId);
        var liqs = (await c.QueryAsync<LiquidacaoRow>(@"SELECT * FROM gestao_liquidacoes
 WHERE estabelecimento_id = @loja AND acontecimento_id = ANY(@chaves);", new { loja, chaves }, tx)).ToLookup(q => q.AcontecimentoId);
        var correcoes = (await c.QueryAsync<(Guid CorrigeId, Guid Id)>(@"SELECT corrige_id, id FROM gestao_acontecimentos
 WHERE estabelecimento_id = @loja AND corrige_id = ANY(@chaves) ORDER BY registrado_em;", new { loja, chaves }, tx))
            .ToLookup(x => x.CorrigeId, x => x.Id);

        return rows.Select(r => new AcontecimentoDto(r.Id, r.Tipo, r.Modulo, r.DataFato, r.RegistradoEm, r.MesReferencia,
            r.ResponsavelId, r.ResponsavelNome, r.OrigemTipo == null ? null : new OrigemRef { Tipo = r.OrigemTipo, Id = r.OrigemId },
            r.DocumentoChave, r.Descricao, JsonDocument.Parse(r.Dados).RootElement.Clone(), r.CorrigeId, r.Correcao, r.Motivo,
            r.ValorAnterior, r.ValorNovo, correcoes[r.Id].ToList(), lancs[r.Id].Select(Dto).ToList(),
            movs[r.Id].Select(Dto).ToList(), liqs[r.Id].Select(Dto).ToList())).ToList();
    }

    public static async Task<List<LancamentoDto>> Lancamentos(NpgsqlConnection c, Guid loja, PeriodoFiltro f, string? fuso) =>
        (await c.QueryAsync<LancamentoRow>($@"
SELECT {LancamentoColunas} FROM gestao_lancamentos_situacao s
  JOIN gestao_acontecimentos a ON a.estabelecimento_id = s.estabelecimento_id AND a.id = s.acontecimento_id
 WHERE s.estabelecimento_id = @loja
   AND (@mes::date IS NULL OR s.mes_referencia = @mes OR date_trunc('month', s.data_vencimento)::date = @mes
        OR date_trunc('month', s.data_pagamento)::date = @mes)
   AND (@dia::date IS NULL OR s.data_pagamento = @dia OR s.data_vencimento = @dia OR (a.data_fato AT TIME ZONE @fuso)::date = @dia)
   AND (@conta::uuid IS NULL OR s.conta_id = @conta OR s.conta_pagamento_id = @conta)
   AND (@status::text IS NULL OR s.status = @status)
   AND (@modulo::text IS NULL OR a.modulo = @modulo)
 ORDER BY coalesce(s.data_pagamento, s.data_vencimento, (a.data_fato AT TIME ZONE @fuso)::date) DESC, s.linha
 LIMIT @limite;",
            new
            {
                loja, mes = f.Mes, dia = f.Dia, fuso = fuso ?? "America/Sao_Paulo", conta = f.ContaId, status = f.Status,
                modulo = f.Modulo, limite = Math.Clamp(f.Limite, 1, 1000)
            })).Select(Dto).ToList();

    public static async Task<List<MovimentoDto>> Movimentos(NpgsqlConnection c, Guid loja, PeriodoFiltro f, string? fuso) =>
        (await c.QueryAsync<MovimentoRow>(@"
SELECT m.* FROM gestao_movimentos_estoque m
 WHERE m.estabelecimento_id = @loja
   AND (@mes::date IS NULL OR m.mes_referencia = @mes)
   AND (@dia::date IS NULL OR (m.confirmado_em AT TIME ZONE @fuso)::date = @dia)
   AND (@item::uuid IS NULL OR m.item_id = @item)
 ORDER BY m.confirmado_em DESC, m.linha LIMIT @limite;",
            new { loja, mes = f.Mes, dia = f.Dia, fuso = fuso ?? "America/Sao_Paulo", item = f.ItemId, limite = Math.Clamp(f.Limite, 1, 1000) }))
        .Select(Dto).ToList();

    /// <summary>Saldo de cada conta = lancamentos ja pagos/recebidos nela + liquidacoes que entraram/sairam dela.</summary>
    public static async Task<List<ContaDto>> Contas(NpgsqlConnection c, Guid loja) =>
        (await c.QueryAsync<ContaRow>(@"
SELECT k.id, k.tipo, k.subtipo, k.nome, k.dia_fechamento, k.dia_vencimento, k.ativo,
       coalesce((SELECT sum(CASE l.sentido WHEN 'entrada' THEN l.valor ELSE -l.valor END) FROM gestao_lancamentos l
                  WHERE l.estabelecimento_id = k.estabelecimento_id AND l.conta_id = k.id AND l.status IN ('pago', 'recebido')), 0)
     + coalesce((SELECT sum(CASE l.sentido WHEN 'entrada' THEN q.valor ELSE -q.valor END) FROM gestao_liquidacoes q
                   JOIN gestao_lancamentos l ON l.estabelecimento_id = q.estabelecimento_id AND l.id = q.lancamento_id
                  WHERE q.estabelecimento_id = k.estabelecimento_id AND q.conta_id = k.id), 0)
     - CASE WHEN k.tipo = 'cartao_empresa' THEN coalesce((SELECT sum(s.valor) FROM gestao_lancamentos_situacao s
                  WHERE s.estabelecimento_id = k.estabelecimento_id AND s.conta_id = k.id AND s.status = 'a_pagar'), 0) ELSE 0 END
       AS saldo
  FROM gestao_contas k WHERE k.estabelecimento_id = @loja ORDER BY k.tipo, k.nome;", new { loja }))
        .Select(r => new ContaDto(r.Id, r.Tipo, r.Subtipo, r.Nome, r.DiaFechamento, r.DiaVencimento, r.Ativo, r.Saldo)).ToList();

    private static LancamentoDto Dto(LancamentoRow r) => new(r.Id, r.AcontecimentoId, r.Linha, r.ContaId, r.Natureza, r.Sentido,
        r.Valor, r.Status, r.StatusRegistro, r.MesReferencia, r.DataVencimento, r.DataPagamento, r.Parcela, r.Parcelas, r.FaturaMes,
        r.Categoria, r.Finalidade, r.Descricao, r.CorrigeLancamentoId, Natureza.ForaDaOperacao(r.Natureza));

    private static MovimentoDto Dto(MovimentoRow r) => new(r.Id, r.AcontecimentoId, r.Linha, r.ItemId, r.ItemNome, r.Quantidade,
        r.Unidade, r.Tipo, r.Motivo, r.MesReferencia, r.ConfirmadoPor, r.ConfirmadoEm, r.CorrigeMovimentoId);

    private static LiquidacaoDto Dto(LiquidacaoRow r) => new(r.Id, r.AcontecimentoId, r.LancamentoId, r.ContaId, r.Valor,
        r.DataPagamento, r.CorrigeLiquidacaoId);
}
