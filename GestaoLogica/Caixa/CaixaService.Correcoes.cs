using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Correções do Caixa sem apagar histórico (6C2-B). Movimento errado: o operador pede, o gerente estorna direto ou aprova o pedido
/// (estorno do livro, o original fica). Fundo de troco errado: transferência cofre ↔ gaveta da diferença (tipo próprio, nunca
/// sangria/suprimento), com antes → depois e motivo; o fundo original não é alterado. Pedido não mexe em dinheiro.
/// </summary>
public sealed partial class CaixaService
{
    public const string TipoCorrecaoFundo = "correcao_fundo_caixa", SolicEstorno = "estorno_movimento", SolicFundo = "corrigir_fundo";
    private const string Pendente = "pendente", Aprovada = "aprovada", Rejeitada = "rejeitada";
    private static readonly string[] TiposEstornaveis = { TipoSangria, TipoSuprimento, TipoPagamento, TipoDevolucao };

    private sealed class SolicitacaoRow
    {
        public Guid Id { get; set; }
        public DateTime Dia { get; set; }
        public string Tipo { get; set; } = "";
        public Guid? AcontecimentoId { get; set; }
        public decimal? FundoAnterior { get; set; }
        public decimal? FundoNovo { get; set; }
        public string Motivo { get; set; } = "";
        public string Status { get; set; } = "";
        public int SolicitadoPor { get; set; }
        public DateTime SolicitadoEm { get; set; }
        public int? DecididoPor { get; set; }
        public DateTime? DecididoEm { get; set; }
        public string? MotivoDecisao { get; set; }
        public Guid? CorrecaoId { get; set; }
        public string? SolicitadoPorNome { get; set; }
        public string? DecididoPorNome { get; set; }
        public string Chave { get; set; } = "";
    }

    private const string ColunasSolicitacao = @"s.id, s.dia, s.tipo, s.acontecimento_id, s.fundo_anterior, s.fundo_novo, s.motivo, s.status,
       s.solicitado_por, s.solicitado_em, s.decidido_por, s.decidido_em, s.motivo_decisao, s.correcao_id, s.chave,
       (SELECT u.nome::text FROM usuario u WHERE u.id = s.solicitado_por) AS solicitado_por_nome,
       (SELECT u.nome::text FROM usuario u WHERE u.id = s.decidido_por) AS decidido_por_nome";

    // ---------------------------------------------------------------- pedido, estorno direto, lista e decisão

    public async Task<SolicitacaoDto> Solicitar(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid movimentoId, SolicitarCorrecaoRequest req,
        CancellationToken ct)
    {
        if (!acesso.PodeLancar) throw SemPermissao("Você não pode pedir correção no caixa.");
        var chave = ChaveDe(req?.ChaveIdempotencia);
        var motivo = MotivoDe(req?.Motivo);
        var hoje = await Hoje(loja, ct);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var (alvo, dia) = await AlvoEstornavel(c, loja, movimentoId, false, ct);
        if (acesso.Papel == GestaoPapeis.OperadorCaixa && dia != hoje) throw SemPermissao("O operador de caixa corrige só o dia de hoje.");
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, $"caixa-solic:{loja}");
        if (await PorChave(c, tx, loja, chave) is { } repetida)
        {
            if (repetida.AcontecimentoId != alvo.Id) throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Esta chave já foi usada com outro pedido.", 409);
            return await ParaDto(c, loja, repetida, ct);
        }
        var id = Guid.NewGuid();
        await Inserir(c, tx, loja, id, chave, dia, SolicEstorno, alvo.Id, null, null, motivo, usuario);
        await tx.CommitAsync(ct);
        return await ParaDto(c, loja, (await Solicitacao(c, null, loja, id))!, ct);
    }

    public async Task<CaixaDoDia> EstornarMovimento(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid movimentoId, EstornarMovimentoRequest req,
        CancellationToken ct)
    {
        GestaoPapeis.ExigirCorrecao(acesso);
        var chave = ChaveDe(req?.ChaveIdempotencia);
        var motivo = MotivoDe(req?.Motivo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var (alvo, dia) = await AlvoEstornavel(c, loja, movimentoId, true, ct);
        var pendente = await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_caixa_solicitacoes
 WHERE estabelecimento_id = @loja AND acontecimento_id = @id AND status = 'pendente')", new { loja, id = alvo.Id });
        if (pendente) throw new GestaoException("SOLICITACAO_DUPLICADA", "Este movimento tem um pedido de correção pendente: decida por ele.", 409);
        await livro.Estornar(loja, usuario, alvo.Id, new EstornoRequest
        {
            ChaveIdempotencia = $"caixa:estorno:{alvo.Id}:{chave}", Motivo = Cortar(motivo, 300), ConfirmacaoHumana = true,
        }, ct);
        return (await CarregarDia(loja, dia, ct))!;
    }

    public async Task<IReadOnlyList<SolicitacaoDto>> Solicitacoes(Guid loja, int usuario, GestaoPapeis.Acesso acesso, string? status, DateOnly? dia,
        CancellationToken ct)
    {
        if (acesso.Papel is null) throw SemPermissao("Você não tem acesso ao caixa.");
        if (status is not null && status is not (Pendente or Aprovada or Rejeitada)) throw Invalido("Situação inválida.");
        var operador = acesso.Papel == GestaoPapeis.OperadorCaixa;
        var dataDia = operador ? await Hoje(loja, ct) : dia;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var linhas = await c.QueryAsync<SolicitacaoRow>($@"SELECT {ColunasSolicitacao} FROM gestao_caixa_solicitacoes s
 WHERE s.estabelecimento_id = @loja AND (CAST(@status AS text) IS NULL OR s.status = @status) AND (CAST(@dia AS date) IS NULL OR s.dia = @dia)
   AND (CAST(@so AS integer) IS NULL OR s.solicitado_por = @so)
 ORDER BY s.solicitado_em DESC, s.id LIMIT 200",
            new { loja, status, dia = dataDia?.ToDateTime(TimeOnly.MinValue), so = operador ? usuario : (int?)null });
        var lista = new List<SolicitacaoDto>();
        foreach (var l in linhas) lista.Add(await ParaDto(c, loja, l, ct));
        return lista;
    }

    public async Task<SolicitacaoDto> Decidir(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid solicitacaoId, DecisaoRequest req,
        CancellationToken ct)
    {
        GestaoPapeis.ExigirCorrecao(acesso);
        var aprovar = req?.Decisao?.Trim().ToLowerInvariant() switch
        {
            "aprovar" => true, "rejeitar" => false, _ => throw Invalido("Decisão: aprovar ou rejeitar."),
        };
        var motivoDecisao = string.IsNullOrWhiteSpace(req!.Motivo) ? null : Cortar(req.Motivo.Trim(), 300);
        if (!aprovar && (motivoDecisao is null || motivoDecisao.Length < 3)) throw Invalido("Diga por que o pedido foi rejeitado (mínimo de 3 letras).");
        var novo = aprovar ? Aprovada : Rejeitada;

        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Uma decisão por vez neste pedido: a segunda espera, vê o estado novo e não repete o efeito.
        await Travar(c, tx, $"caixa-decisao:{loja}:{solicitacaoId}");
        var s = await Solicitacao(c, tx, loja, solicitacaoId) ?? throw NaoEncontrado("Pedido de correção");
        if (s.Status != Pendente)
        {
            if (s.Status == novo) return await ParaDto(c, loja, s, ct);
            throw new GestaoException("JA_DECIDIDA", "Este pedido já foi decidido.", 409);
        }
        Guid? correcao = null;
        if (aprovar)
        {
            var dia = DateOnly.FromDateTime(s.Dia);
            if (s.Tipo == SolicEstorno)
            {
                // O estorno é do livro (outra transação): a chave fixa do pedido garante que uma nova tentativa não duplique.
                var (alvo, _) = await AlvoEstornavel(c, loja, s.AcontecimentoId!.Value, true, ct);
                correcao = (await livro.Estornar(loja, usuario, alvo.Id, new EstornoRequest
                {
                    ChaveIdempotencia = $"caixa:correcao:{solicitacaoId}", Motivo = Cortar(s.Motivo, 300), ConfirmacaoHumana = true,
                }, ct)).Id;
            }
            else
                correcao = await AplicarFundo(c, tx, loja, usuario, acesso, dia, s.FundoAnterior!.Value, s.FundoNovo!.Value, s.Motivo,
                    solicitacaoId, $"solic-{solicitacaoId:N}", ct);
        }
        var n = await c.ExecuteAsync(@"
UPDATE gestao_caixa_solicitacoes SET status = @novo, decidido_por = @usuario, decidido_em = @agora, motivo_decisao = @motivoDecisao,
       correcao_id = @correcao
 WHERE estabelecimento_id = @loja AND id = @id AND status = 'pendente'",
            new { novo, usuario, agora = _relogio.GetUtcNow(), motivoDecisao, correcao, loja, id = solicitacaoId }, tx);
        if (n == 0) throw new GestaoException("JA_DECIDIDA", "Este pedido já foi decidido.", 409);
        await tx.CommitAsync(ct);
        return await ParaDto(c, loja, (await Solicitacao(c, null, loja, solicitacaoId))!, ct);
    }

    // ---------------------------------------------------------------- fundo de troco

    public async Task<CorrigirFundoResposta> CorrigirFundo(Guid loja, int usuario, GestaoPapeis.Acesso acesso, DateOnly dia, CorrigirFundoRequest req,
        CancellationToken ct)
    {
        if (!acesso.PodeLancar) throw SemPermissao("Você não pode corrigir o caixa.");
        var chave = ChaveDe(req?.ChaveIdempotencia);
        var motivo = MotivoDe(req?.Motivo);
        if (req!.FundoNovo < 0 || decimal.Round(req.FundoNovo, 2) != req.FundoNovo) throw Invalido("Fundo de troco: zero ou mais, até 2 casas.");
        var hoje = await SoHoje(loja, dia, ct);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await DiaAberto(c, loja, hoje);
        await using var tx = await c.BeginTransactionAsync(ct);

        if (!acesso.PodeCorrigir)
        {
            // Operador: só pede. O dinheiro não se move antes da aprovação do gerente.
            await Travar(c, tx, $"caixa-solic:{loja}");
            if (await PorChave(c, tx, loja, chave) is { } repetida)
            {
                if (repetida.Tipo != SolicFundo || repetida.FundoNovo != req.FundoNovo)
                    throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Esta chave já foi usada com outro pedido.", 409);
                return new CorrigirFundoResposta(false, null, await ParaDto(c, loja, repetida, ct));
            }
            await Travar(c, tx, $"caixa-fundo:{loja}:{hoje:yyyy-MM-dd}");
            var efetivo = await FundoEfetivo(c, tx, loja, hoje);
            if (efetivo != req.FundoAtual) throw new GestaoException("CONFLITO", "O fundo mudou desde que você abriu esta tela.", 409);
            if (req.FundoNovo == efetivo) throw Invalido("O fundo novo é igual ao atual.");
            var id = Guid.NewGuid();
            await Inserir(c, tx, loja, id, chave, hoje, SolicFundo, null, efetivo, req.FundoNovo, motivo, usuario);
            await tx.CommitAsync(ct);
            return new CorrigirFundoResposta(false, null, await ParaDto(c, loja, (await Solicitacao(c, null, loja, id))!, ct));
        }

        var antes = await c.ExecuteScalarAsync<Guid?>("SELECT id FROM gestao_caixa_fundo_ajustes WHERE estabelecimento_id = @loja AND chave = @chave",
            new { loja, chave }, tx);
        if (antes is not null) return new CorrigirFundoResposta(true, await CarregarDia(loja, hoje, ct), null);
        // O gerente efetiva; pedido pendente de fundo do dia fica para ele decidir (uma correção por vez).
        if (await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_caixa_solicitacoes WHERE estabelecimento_id = @loja
 AND dia = @dia AND tipo = 'corrigir_fundo' AND status = 'pendente')", new { loja, dia = hoje.ToDateTime(TimeOnly.MinValue) }, tx))
            throw new GestaoException("SOLICITACAO_DUPLICADA", "Há um pedido de correção do fundo pendente: decida por ele.", 409);
        await AplicarFundo(c, tx, loja, usuario, acesso, hoje, req.FundoAtual, req.FundoNovo, motivo, null, chave, ct);
        await tx.CommitAsync(ct);
        return new CorrigirFundoResposta(true, await CarregarDia(loja, hoje, ct), null);
    }

    /// <summary>
    /// Grava a correção do fundo na transação de quem chama: lock do dia, confere o fundo efetivo, transfere a diferença entre cofre e
    /// gaveta (tipo próprio) e registra antes → depois. Devolve o acontecimento criado.
    /// </summary>
    private async Task<Guid> AplicarFundo(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, GestaoPapeis.Acesso acesso, DateOnly dia,
        decimal fundoAtual, decimal fundoNovo, string motivo, Guid? solicitacao, string chave, CancellationToken ct)
    {
        var hoje = await Hoje(loja, ct);
        if (dia != hoje) throw new GestaoException("FECHADO", "O caixa deste dia não aceita mais correção do fundo.", 409);
        await Travar(c, tx, $"caixa-fundo:{loja}:{dia:yyyy-MM-dd}");
        var row = await c.QuerySingleOrDefaultAsync<DiaRow>(
            "SELECT dia, fundo, fechado_em FROM gestao_caixa_dias WHERE estabelecimento_id = @loja AND dia = @dia FOR UPDATE",
            new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) }, tx);
        if (row is null) throw new GestaoException("SEM_CAIXA", "Abra o caixa antes.", 409);
        if (row.FechadoEm is not null) throw new GestaoException("FECHADO", "O caixa de hoje já foi fechado.", 409);
        var efetivo = await FundoEfetivo(c, tx, loja, dia);
        if (efetivo != fundoAtual) throw new GestaoException("CONFLITO", "O fundo mudou desde que o pedido foi feito.", 409);
        if (fundoNovo == efetivo) throw Invalido("O fundo novo é igual ao atual.");
        var diferenca = CaixaRegras.R2(fundoNovo - efetivo);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        if (diferenca < 0 && await Saldo(loja, contas.Gaveta, ct) < -diferenca)
            throw new GestaoException("SALDO_INSUFICIENTE", "A gaveta não tem esse valor para voltar ao cofre.", 422);
        var ajusteId = Guid.NewGuid();
        var ordem = await c.ExecuteScalarAsync<int>("SELECT coalesce(max(ordem), 0) + 1 FROM gestao_caixa_fundo_ajustes WHERE estabelecimento_id = @loja AND dia = @dia",
            new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) }, tx);
        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"caixa:fundo:{dia:yyyy-MM-dd}:{chave}", Tipo = TipoCorrecaoFundo, Modulo = ModuloGestao.Caixa,
            DataFato = _relogio.GetUtcNow(), Descricao = Cortar($"Correção do fundo de troco · R$ {efetivo:N2} → R$ {fundoNovo:N2} · {motivo}", 300),
            Origem = new OrigemRef { Tipo = "caixa_fundo", Id = ajusteId.ToString() },
            Dados = JsonSerializer.SerializeToElement(new { dia, fundoAnterior = efetivo, fundoNovo, motivo, solicitacaoId = solicitacao }, Json),
            Lancamentos = diferenca > 0
                ? Transferencia(contas.Cofre, contas.Gaveta, diferenca, dia, "Correção do fundo de troco")
                : Transferencia(contas.Gaveta, contas.Cofre, -diferenca, dia, "Correção do fundo de troco"),
        };
        GestaoPapeis.ValidarPublicacao(acesso, pedido, dia, dia);
        var r = await livro.Publicar(c, tx, loja, usuario, pedido, ct);
        await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_fundo_ajustes (id, estabelecimento_id, chave, dia, ordem, por, em, fundo_anterior, fundo_novo, motivo, acontecimento_id, solicitacao_id)
VALUES (@ajusteId, @loja, @chave, @dia, @ordem, @usuario, @agora, @efetivo, @fundoNovo, @motivo, @acontecimento, @solicitacao)",
            new
            {
                ajusteId, loja, chave, dia = dia.ToDateTime(TimeOnly.MinValue), ordem, usuario, agora = _relogio.GetUtcNow(), efetivo, fundoNovo,
                motivo, acontecimento = r.Id, solicitacao,
            }, tx);
        return r.Id;
    }

    private static async Task<decimal> FundoEfetivo(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, DateOnly dia) =>
        await c.ExecuteScalarAsync<decimal>(@"
SELECT coalesce((SELECT fundo_novo FROM gestao_caixa_fundo_ajustes WHERE estabelecimento_id = @loja AND dia = @dia ORDER BY ordem DESC LIMIT 1),
                (SELECT fundo FROM gestao_caixa_dias WHERE estabelecimento_id = @loja AND dia = @dia))",
            new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) }, tx);

    private static async Task<IReadOnlyList<CorrecaoFundoDto>> CorrecoesDoFundo(NpgsqlConnection c, Guid loja, DateOnly dia) =>
        (await c.QueryAsync<(Guid Id, DateTime Em, int Por, string? PorNome, decimal Anterior, decimal Novo, string Motivo)>(@"
SELECT a.id, a.em, a.por, u.nome::text AS por_nome, a.fundo_anterior, a.fundo_novo, a.motivo
  FROM gestao_caixa_fundo_ajustes a LEFT JOIN usuario u ON u.id = a.por
 WHERE a.estabelecimento_id = @loja AND a.dia = @dia ORDER BY a.ordem", new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) }))
            .Select(a => new CorrecaoFundoDto(a.Id, dia, Utc(a.Em), a.Por, a.PorNome, a.Anterior, a.Novo, a.Motivo)).ToList();

    // ---------------------------------------------------------------- apoio

    /// <summary>Movimentos do dia com pedido pendente (acontecimento → pedido) e o total de pendentes (inclui o do fundo).</summary>
    private static async Task<(Dictionary<Guid, Guid> Movimentos, int Total)> PendentesPorMovimento(NpgsqlConnection c, Guid loja, DateOnly dia)
    {
        var linhas = (await c.QueryAsync<(Guid Id, Guid? Acontecimento)>(@"SELECT id, acontecimento_id FROM gestao_caixa_solicitacoes
 WHERE estabelecimento_id = @loja AND dia = @dia AND status = 'pendente'", new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) })).ToList();
        return (linhas.Where(l => l.Acontecimento is not null).ToDictionary(l => l.Acontecimento!.Value, l => l.Id), linhas.Count);
    }

    /// <summary>Estornável: movimento de gaveta (sangria, suprimento, pagamento, devolução, ou liquidação paga pela gaveta). Nunca venda.</summary>
    private static bool Estornavel(AcontecimentoDto a, Guid gaveta) =>
        a.Correcao is null && a.CorrigeId is null && a.Modulo != ModuloGestao.Pedidos &&
        (TiposEstornaveis.Contains(a.Tipo) ||
         (a.Tipo == TipoAcontecimento.Pagamento && a.Liquidacoes.Any(q => q.ContaId == gaveta && q.CorrigeLiquidacaoId is null)));

    /// <summary>
    /// O movimento a corrigir: da loja, de gaveta, não estornado e de um dia ainda aberto. <paramref name="aceitaEstornado"/> deixa a
    /// repetição de uma aprovação passar (quem decide é a chave do livro).
    /// </summary>
    private async Task<(AcontecimentoDto Alvo, DateOnly Dia)> AlvoEstornavel(NpgsqlConnection c, Guid loja, Guid id, bool aceitaEstornado,
        CancellationToken ct)
    {
        var a = await livro.Obter(loja, id, ct) ?? throw NaoEncontrado("Movimento");
        var contas = await ContasDoCaixa(c, loja, null, ct);
        if (!Estornavel(a, contas.Gaveta))
            throw new GestaoException("NAO_ELEGIVEL", "Este lançamento não pode ser corrigido pelo caixa.", 422);
        if (a.CorrigidoPor.Count > 0 && !aceitaEstornado) throw new GestaoException("JA_ESTORNADO", "Este movimento já foi estornado.", 409);
        var dia = AcontecimentoRegras.DiaLocal(a.DataFato, await livro.Fuso(loja, ct));
        var d = await Dia(c, loja, dia) ?? throw new GestaoException("NAO_ELEGIVEL", "Este movimento não é de um dia de caixa.", 422);
        if (d.FechadoEm is not null) throw new GestaoException("FECHADO", "O caixa deste dia já foi fechado: não aceita correção.", 409);
        return (a, dia);
    }

    private static Task Inserir(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id, string chave, DateOnly dia, string tipo, Guid? alvo,
        decimal? anterior, decimal? novo, string motivo, int usuario)
    {
        return Executar();
        async Task Executar()
        {
            try
            {
                await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_solicitacoes (id, estabelecimento_id, chave, dia, tipo, acontecimento_id, fundo_anterior, fundo_novo, motivo, solicitado_por, solicitado_em)
VALUES (@id, @loja, @chave, @dia, @tipo, @alvo, @anterior, @novo, @motivo, @usuario, now())",
                    new { id, loja, chave, dia = dia.ToDateTime(TimeOnly.MinValue), tipo, alvo, anterior, novo, motivo, usuario }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new GestaoException("SOLICITACAO_DUPLICADA", "Já existe um pedido de correção pendente para isto.", 409);
            }
        }
    }

    private static Task<SolicitacaoRow?> Solicitacao(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid id) =>
        c.QuerySingleOrDefaultAsync<SolicitacaoRow>($@"SELECT {ColunasSolicitacao} FROM gestao_caixa_solicitacoes s
 WHERE s.estabelecimento_id = @loja AND s.id = @id" + (tx is null ? "" : " FOR UPDATE OF s"), new { loja, id }, tx);

    private static Task<SolicitacaoRow?> PorChave(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, string chave) =>
        c.QuerySingleOrDefaultAsync<SolicitacaoRow>($@"SELECT {ColunasSolicitacao} FROM gestao_caixa_solicitacoes s
 WHERE s.estabelecimento_id = @loja AND s.chave = @chave", new { loja, chave }, tx);

    private static Task Travar(NpgsqlConnection c, NpgsqlTransaction tx, string chave) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@chave, 0))", new { chave }, tx);

    private async Task<SolicitacaoDto> ParaDto(NpgsqlConnection c, Guid loja, SolicitacaoRow r, CancellationToken ct)
    {
        MovimentoSolicitado? movimento = null;
        if (r.AcontecimentoId is { } id && await livro.Obter(loja, id, ct) is { } a)
        {
            var gaveta = (await ContasDoCaixa(c, loja, null, ct)).Gaveta;
            var valor = a.Lancamentos.Where(l => l.ContaId == gaveta).Sum(l => l.Valor);
            if (valor == 0) valor = a.Liquidacoes.Where(q => q.ContaId == gaveta).Sum(q => q.Valor);
            if (valor == 0) valor = a.Lancamentos.Where(l => l.CorrigeLancamentoId is null).Sum(l => l.Valor);
            movimento = new MovimentoSolicitado(a.Id, a.Tipo, a.Descricao, a.DataFato, valor);
        }
        return new SolicitacaoDto(r.Id, DateOnly.FromDateTime(r.Dia), r.Tipo, r.Status, movimento, r.FundoAnterior, r.FundoNovo, r.Motivo,
            r.SolicitadoPor, r.SolicitadoPorNome, Utc(r.SolicitadoEm), r.DecididoPor, r.DecididoPorNome,
            r.DecididoEm is { } em ? Utc(em) : null, r.MotivoDecisao, r.CorrecaoId);
    }

    private static string ChaveDe(string? chave)
    {
        var limpa = chave?.Trim();
        if (string.IsNullOrEmpty(limpa)) throw Invalido("Envie a chave de idempotência.");
        if (limpa.Length > 60) throw Invalido("Chave de idempotência longa demais (até 60).");
        return limpa;
    }

    private static string MotivoDe(string? motivo)
    {
        var limpo = motivo?.Trim();
        if (limpo is null || limpo.Length < 3) throw Invalido("Diga o motivo da correção (mínimo de 3 letras).");
        return Cortar(limpo, 300);
    }

    private static GestaoException SemPermissao(string m) => new("SEM_PERMISSAO", m, 403);

    private static GestaoException NaoEncontrado(string o) => new("NAO_ENCONTRADO", $"{o} não encontrado.", 404);
}
