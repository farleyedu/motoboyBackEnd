using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.Equipe.Ponto;

/// <summary>
/// Ponto em tempo real (P02, CONTRATO-API §1.2 e §3.2): entrar, pausar, voltar e sair, com carimbo do servidor. Uma pessoa por vez
/// (trava por pessoa + índice único do ponto aberto). Ao sair, preenche <c>gestao_turnos</c> pela Equipe sem apagar o que o
/// gerente decidiu e avisa <c>equipe.ponto.mudou</c> depois do commit.
/// </summary>
public interface IPontoService
{
    Task<PontoAtor> Ator(Guid loja, int usuario, bool gestorPeloPainel, CancellationToken ct);
    Task<PontoEntrouDto> Entrar(PontoAtor ator, PontoEntrarRequest req, CancellationToken ct);
    Task<PontoPausaFeitaDto> Pausar(PontoAtor ator, PontoPausarRequest req, CancellationToken ct);
    Task<PontoPausaFeitaDto> Voltar(PontoAtor ator, PontoVoltarRequest req, CancellationToken ct);
    Task<PontoSaiuDto> Sair(PontoAtor ator, PontoSairRequest req, CancellationToken ct);
    Task<PontoDiaDto> Dia(PontoAtor ator, Guid? pessoaId, DateOnly? data, CancellationToken ct);
    Task<IReadOnlyList<PontoDto>> Abertos(PontoAtor ator, CancellationToken ct);
}

public sealed class PontoService(NpgsqlDataSource source, IPontoEquipe equipe, IPontoTempoReal tempoReal, TimeProvider? relogio = null)
    : IPontoService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    private sealed class PessoaRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string? FuncaoCodigo { get; set; }
        public string Vinculo { get; set; } = "";
        public bool Ativo { get; set; }
    }

    private sealed class PontoRow
    {
        public Guid Id { get; set; }
        public Guid PessoaId { get; set; }
        public string PessoaNome { get; set; } = "";
        public DateTime Dia { get; set; }
        public string Modo { get; set; } = "";
        public DateTime Entrada { get; set; }
        public DateTime? Saida { get; set; }
        public string? FechadoPor { get; set; }
        public string? MotivoSaida { get; set; }
        public string? TurnoSituacao { get; set; }
        public string? TurnoEntrada { get; set; }
        public string? TurnoSaida { get; set; }
        public string? TurnoAviso { get; set; }
    }

    private sealed class PausaRow
    {
        public Guid Id { get; set; }
        public Guid PontoId { get; set; }
        public DateTime Inicio { get; set; }
        public DateTime? Fim { get; set; }
        public string? Motivo { get; set; }
    }

    private const string ColunasPonto = """
        p.id, p.pessoa_id AS PessoaId, g.nome AS PessoaNome, p.dia, p.modo, p.entrada, p.saida, p.fechado_por AS FechadoPor,
        p.motivo_saida AS MotivoSaida, p.turno_situacao AS TurnoSituacao, p.turno_entrada AS TurnoEntrada, p.turno_saida AS TurnoSaida,
        p.turno_aviso AS TurnoAviso
        """;

    public async Task<PontoAtor> Ator(Guid loja, int usuario, bool gestorPeloPainel, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var pessoa = await c.QueryFirstOrDefaultAsync<(Guid Id, string? Funcao)?>(new CommandDefinition("""
            SELECT id, funcao_codigo FROM gestao_pessoas
             WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND ativo
             ORDER BY atualizado_em DESC LIMIT 1
            """, new { loja, usuario }, cancellationToken: ct));
        return new PontoAtor(loja, usuario, pessoa?.Id, gestorPeloPainel || pessoa?.Funcao == "gerente_salao");
    }

    public async Task<PontoEntrouDto> Entrar(PontoAtor ator, PontoEntrarRequest req, CancellationToken ct)
    {
        var pessoaId = Alvo(ator, req.PessoaId);
        var fuso = await equipe.Fuso(ator.Loja, ct);
        Guid id;
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            var pessoa = await Pessoa(c, tx, ator.Loja, pessoaId);
            var modo = PontoRegras.Limpar(req.Modo) ?? PontoRegras.ModoPadrao(pessoa.FuncaoCodigo, pessoa.Vinculo)
                ?? throw PontoException.Validacao("Escolha o modo: garçom, motoboy, cozinha ou caixa.");
            if (!PontoRegras.Modos.Contains(modo, StringComparer.Ordinal))
                throw PontoException.Validacao("Modo deve ser garcom, motoboy, cozinha ou caixa.");
            await Travar(c, tx, pessoaId);
            if (await Aberto(c, tx, ator.Loja, pessoaId) is { } aberto)
                throw JaFeito(aberto, fuso);
            var agora = _relogio.GetUtcNow();
            id = Guid.NewGuid();
            try
            {
                await c.ExecuteAsync("""
                    INSERT INTO gestao_ponto (id, estabelecimento_id, pessoa_id, dia, modo, entrada, entrada_por)
                    VALUES (@id, @loja, @pessoaId, @dia, @modo, @agora, @usuario)
                    """, new { id, loja = ator.Loja, pessoaId, dia = PontoRegras.DiaLocal(agora, fuso).ToDateTime(TimeOnly.MinValue), modo,
                    agora, usuario = ator.Usuario }, tx);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Outra loja com a mesma pessoa não existe (id é único); aqui só chega corrida que escapou da trava.
                throw new PontoException("JA_FEITO", "O ponto já está aberto.", 409);
            }
            await tx.CommitAsync(ct);
        }
        await tempoReal.Mudou(ator.Loja, pessoaId);
        var ponto = await Ler(ator.Loja, id, fuso, ct);
        return new PontoEntrouDto(id, ponto.Entrada, ponto);
    }

    public async Task<PontoPausaFeitaDto> Pausar(PontoAtor ator, PontoPausarRequest req, CancellationToken ct)
    {
        var pessoaId = Alvo(ator, req.PessoaId);
        var motivo = PontoRegras.Limpar(req.Motivo);
        if (motivo?.Length > PontoRegras.MotivoPausaMax) throw PontoException.Validacao($"Motivo da pausa: até {PontoRegras.MotivoPausaMax} letras.");
        var fuso = await equipe.Fuso(ator.Loja, ct);
        Guid pontoId, pausaId;
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            await Pessoa(c, tx, ator.Loja, pessoaId);
            await Travar(c, tx, pessoaId);
            var aberto = await Aberto(c, tx, ator.Loja, pessoaId) ?? throw PontoException.EstadoInvalido("Bata o ponto antes de pausar.");
            pontoId = aberto.Id;
            var pausa = await PausaAberta(c, tx, pontoId);
            if (pausa is not null)
                throw PontoException.EstadoInvalido($"Já está em pausa desde {PontoRegras.HoraLocal(Utc(pausa.Inicio), fuso)}.");
            pausaId = Guid.NewGuid();
            await c.ExecuteAsync("""
                INSERT INTO gestao_ponto_pausa (id, estabelecimento_id, ponto_id, inicio, motivo) VALUES (@pausaId, @loja, @pontoId, @agora, @motivo)
                """, new { pausaId, loja = ator.Loja, pontoId, agora = _relogio.GetUtcNow(), motivo }, tx);
            await tx.CommitAsync(ct);
        }
        await tempoReal.Mudou(ator.Loja, pessoaId);
        return new PontoPausaFeitaDto(pausaId, await Ler(ator.Loja, pontoId, fuso, ct));
    }

    public async Task<PontoPausaFeitaDto> Voltar(PontoAtor ator, PontoVoltarRequest req, CancellationToken ct)
    {
        var pessoaId = Alvo(ator, req.PessoaId);
        var fuso = await equipe.Fuso(ator.Loja, ct);
        Guid pontoId, pausaId;
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            await Pessoa(c, tx, ator.Loja, pessoaId);
            await Travar(c, tx, pessoaId);
            var aberto = await Aberto(c, tx, ator.Loja, pessoaId) ?? throw PontoException.EstadoInvalido("Não há ponto aberto.");
            pontoId = aberto.Id;
            var pausa = await PausaAberta(c, tx, pontoId) ?? throw PontoException.EstadoInvalido("Não está em pausa.");
            pausaId = pausa.Id;
            await FecharPausa(c, tx, pausa);
            await tx.CommitAsync(ct);
        }
        await tempoReal.Mudou(ator.Loja, pessoaId);
        return new PontoPausaFeitaDto(pausaId, await Ler(ator.Loja, pontoId, fuso, ct));
    }

    public async Task<PontoSaiuDto> Sair(PontoAtor ator, PontoSairRequest req, CancellationToken ct)
    {
        var pessoaId = Alvo(ator, req.PessoaId);
        var deOutro = ator.PessoaId != pessoaId;
        var motivo = PontoRegras.Limpar(req.Motivo);
        if (motivo?.Length > PontoRegras.MotivoMax) throw PontoException.Validacao($"Motivo: até {PontoRegras.MotivoMax} letras.");
        if (deOutro && (motivo?.Length ?? 0) < 3)
            throw new PontoException("EXIGE_MOTIVO", "Diga o motivo de encerrar o ponto de outra pessoa.", 422);
        var fuso = await equipe.Fuso(ator.Loja, ct);
        PontoRow aberto;
        DateTimeOffset saida;
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            await using var tx = await c.BeginTransactionAsync(ct);
            await Pessoa(c, tx, ator.Loja, pessoaId);
            await Travar(c, tx, pessoaId);
            aberto = await Aberto(c, tx, ator.Loja, pessoaId) ?? throw PontoException.EstadoInvalido("Não há ponto aberto.");
            if (await PausaAberta(c, tx, aberto.Id) is { } pausa) await FecharPausa(c, tx, pausa);
            // Carimbo do servidor; no mesmo segundo da entrada, a saída fica um segundo depois (a tabela exige saída > entrada).
            var agora = _relogio.GetUtcNow();
            var entrada = Utc(aberto.Entrada);
            saida = agora > entrada ? agora : entrada.AddSeconds(1);
            await c.ExecuteAsync("""
                UPDATE gestao_ponto SET saida = @saida, saida_por = @usuario, fechado_por = @por, motivo_saida = @motivo
                 WHERE id = @id AND saida IS NULL
                """, new { saida, usuario = ator.Usuario, por = deOutro ? "gerente" : "pessoa", motivo, id = aberto.Id }, tx);
            await tx.CommitAsync(ct);
        }

        await AplicarTurno(ator, aberto, saida, fuso, ct);
        await tempoReal.Mudou(ator.Loja, pessoaId);
        var ponto = await Ler(ator.Loja, aberto.Id, fuso, ct);
        // Mesas atendidas entram quando a sessão da mesa existir (P07).
        return new PontoSaiuDto(new PontoResumoDto(0, ponto.MinutosTrabalhados), ponto);
    }

    public async Task<PontoDiaDto> Dia(PontoAtor ator, Guid? pessoaId, DateOnly? data, CancellationToken ct)
    {
        var alvo = Alvo(ator, pessoaId);
        var fuso = await equipe.Fuso(ator.Loja, ct);
        var agora = _relogio.GetUtcNow();
        var hoje = PontoRegras.DiaLocal(agora, fuso);
        var dia = data ?? hoje;
        await using var c = await source.OpenConnectionAsync(ct);
        await Pessoa(c, null, ator.Loja, alvo);
        // Pontos do dia + o aberto (que pode ter começado ontem, na virada).
        var rows = (await c.QueryAsync<PontoRow>(new CommandDefinition($"""
            SELECT {ColunasPonto} FROM gestao_ponto p JOIN gestao_pessoas g ON g.id = p.pessoa_id
             WHERE p.estabelecimento_id = @loja AND p.pessoa_id = @alvo AND (p.dia = @dia OR (p.saida IS NULL AND @ehHoje))
             ORDER BY p.entrada
            """, new { loja = ator.Loja, alvo, dia = dia.ToDateTime(TimeOnly.MinValue), ehHoje = dia == hoje }, cancellationToken: ct))).ToList();
        var pontos = await Montar(c, rows, fuso, agora, ct);
        var aberto = pontos.FirstOrDefault(p => p.Saida is null);
        var doDia = pontos.Where(p => p.Dia == dia).ToList();
        return new PontoDiaDto(alvo, dia, aberto?.Estado ?? PontoRegras.Encerrado, aberto, doDia,
            doDia.Sum(p => p.MinutosTrabalhados), doDia.Sum(p => p.MinutosPausa));
    }

    public async Task<IReadOnlyList<PontoDto>> Abertos(PontoAtor ator, CancellationToken ct)
    {
        if (!ator.Gestor) throw PontoException.SemPermissao("Quem está com ponto agora é do gerente.");
        var fuso = await equipe.Fuso(ator.Loja, ct);
        await using var c = await source.OpenConnectionAsync(ct);
        var rows = (await c.QueryAsync<PontoRow>(new CommandDefinition($"""
            SELECT {ColunasPonto} FROM gestao_ponto p JOIN gestao_pessoas g ON g.id = p.pessoa_id
             WHERE p.estabelecimento_id = @loja AND p.saida IS NULL ORDER BY g.nome
            """, new { loja = ator.Loja }, cancellationToken: ct))).ToList();
        return await Montar(c, rows, fuso, _relogio.GetUtcNow(), ct);
    }

    // ---------- turno ----------

    private async Task AplicarTurno(PontoAtor ator, PontoRow ponto, DateTimeOffset saida, TimeZoneInfo fuso, CancellationToken ct)
    {
        var dia = DateOnly.FromDateTime(ponto.Dia);
        var entradaHora = PontoRegras.HoraLocal(Utc(ponto.Entrada), fuso);
        var saidaHora = PontoRegras.HoraLocal(saida, fuso);
        DecisaoTurno decisao;
        await using (var c = await source.OpenConnectionAsync(ct))
        {
            var turno = await c.QueryFirstOrDefaultAsync<(string Status, string Entrada, string Saida, Guid? Acontecimento)?>(new CommandDefinition("""
                SELECT status, entrada, saida, acontecimento_id FROM gestao_turnos
                 WHERE estabelecimento_id = @loja AND pessoa_id = @pessoa AND data = @dia
                """, new { loja = ator.Loja, pessoa = ponto.PessoaId, dia = ponto.Dia }, cancellationToken: ct));
            var anterior = await c.QueryFirstOrDefaultAsync<(string? Situacao, string? Entrada, string? Saida)?>(new CommandDefinition("""
                SELECT turno_situacao, turno_entrada, turno_saida FROM gestao_ponto
                 WHERE estabelecimento_id = @loja AND pessoa_id = @pessoa AND dia = @dia AND id <> @id AND saida IS NOT NULL
                 ORDER BY saida DESC LIMIT 1
                """, new { loja = ator.Loja, pessoa = ponto.PessoaId, dia = ponto.Dia, id = ponto.Id }, cancellationToken: ct));
            decisao = PontoRegras.DecidirTurno(
                turno is { } t ? new TurnoAtual(t.Status, t.Entrada, t.Saida, t.Acontecimento is not null) : null,
                entradaHora, saidaHora,
                anterior is { } a ? new PontoAnteriorNoDia(a.Situacao, a.Entrada, a.Saida) : null);
        }

        string situacao;
        string? aviso = decisao.Aviso, te = null, ts = null;
        if (decisao.Acao == AcaoTurno.Preencher)
        {
            try
            {
                await equipe.PreencherTurno(ator.Loja, ator.Usuario, ponto.PessoaId, dia, decisao.Entrada!, decisao.Saida!, decisao.Motivo, ct);
                (situacao, te, ts) = (PontoRegras.Preenchido, decisao.Entrada, decisao.Saida);
            }
            catch (GestaoException ex)
            {
                // O ponto fica fechado; o gerente confirma o turno à mão em Equipe e remuneração.
                (situacao, aviso) = (PontoRegras.NaoAplicado, ex.Message);
            }
        }
        else situacao = decisao.Acao == AcaoTurno.Manter ? PontoRegras.Mantido : PontoRegras.NaoAplicado;

        await using var w = await source.OpenConnectionAsync(ct);
        await w.ExecuteAsync(new CommandDefinition("""
            UPDATE gestao_ponto SET turno_situacao = @situacao, turno_entrada = @te, turno_saida = @ts, turno_aviso = @aviso WHERE id = @id
            """, new { situacao, te, ts, aviso = aviso is { Length: > 300 } ? aviso[..300] : aviso, id = ponto.Id }, cancellationToken: ct));
    }

    // ---------- apoio ----------

    private static Guid Alvo(PontoAtor ator, Guid? pedido)
    {
        if (pedido is { } id && id != Guid.Empty)
        {
            if (id != ator.PessoaId && !ator.Gestor) throw PontoException.SemPermissao();
            return id;
        }
        return ator.PessoaId ?? throw PontoException.SemPermissao("Seu usuário não está na Equipe desta loja: peça ao gerente para ligar.");
    }

    private static async Task<PessoaRow> Pessoa(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid pessoaId)
    {
        var p = await c.QuerySingleOrDefaultAsync<PessoaRow>("""
            SELECT id, nome, funcao_codigo AS FuncaoCodigo, vinculo, ativo FROM gestao_pessoas WHERE estabelecimento_id = @loja AND id = @pessoaId
            """, new { loja, pessoaId }, tx) ?? throw PontoException.NaoEncontrado("Pessoa não encontrada nesta loja.");
        if (!p.Ativo) throw PontoException.EstadoInvalido($"{p.Nome} está inativa na Equipe.");
        return p;
    }

    private static Task Travar(NpgsqlConnection c, NpgsqlTransaction tx, Guid pessoaId) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext(@k))", new { k = "gestao-ponto:" + pessoaId }, tx);

    private static Task<PontoRow?> Aberto(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid pessoaId) =>
        c.QuerySingleOrDefaultAsync<PontoRow?>($"""
            SELECT {ColunasPonto} FROM gestao_ponto p JOIN gestao_pessoas g ON g.id = p.pessoa_id
             WHERE p.estabelecimento_id = @loja AND p.pessoa_id = @pessoaId AND p.saida IS NULL
            """, new { loja, pessoaId }, tx);

    private static Task<PausaRow?> PausaAberta(NpgsqlConnection c, NpgsqlTransaction tx, Guid pontoId) =>
        c.QuerySingleOrDefaultAsync<PausaRow?>(
            "SELECT id, ponto_id AS PontoId, inicio, fim, motivo FROM gestao_ponto_pausa WHERE ponto_id = @pontoId AND fim IS NULL",
            new { pontoId }, tx);

    private Task FecharPausa(NpgsqlConnection c, NpgsqlTransaction tx, PausaRow pausa)
    {
        var agora = _relogio.GetUtcNow();
        var inicio = Utc(pausa.Inicio);
        return c.ExecuteAsync("UPDATE gestao_ponto_pausa SET fim = @fim WHERE id = @id AND fim IS NULL",
            new { fim = agora > inicio ? agora : inicio, id = pausa.Id }, tx);
    }

    private static PontoException JaFeito(PontoRow aberto, TimeZoneInfo fuso)
    {
        var hora = PontoRegras.HoraLocal(Utc(aberto.Entrada), fuso);
        return new PontoException("JA_FEITO", $"O ponto já está aberto desde {hora}.", 409)
        {
            Detalhe = new { conflito = true, quando = Utc(aberto.Entrada), pontoId = aberto.Id },
        };
    }

    private async Task<PontoDto> Ler(Guid loja, Guid id, TimeZoneInfo fuso, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await c.QuerySingleAsync<PontoRow>(new CommandDefinition($"""
            SELECT {ColunasPonto} FROM gestao_ponto p JOIN gestao_pessoas g ON g.id = p.pessoa_id WHERE p.estabelecimento_id = @loja AND p.id = @id
            """, new { loja, id }, cancellationToken: ct));
        return (await Montar(c, new List<PontoRow> { row }, fuso, _relogio.GetUtcNow(), ct))[0];
    }

    private static async Task<IReadOnlyList<PontoDto>> Montar(NpgsqlConnection c, List<PontoRow> rows, TimeZoneInfo fuso, DateTimeOffset agora,
        CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<PontoDto>();
        var pausas = (await c.QueryAsync<PausaRow>(new CommandDefinition("""
            SELECT id, ponto_id AS PontoId, inicio, fim, motivo FROM gestao_ponto_pausa WHERE ponto_id = ANY(@ids) ORDER BY inicio
            """, new { ids = rows.Select(r => r.Id).ToArray() }, cancellationToken: ct))).ToLookup(p => p.PontoId);
        return rows.Select(r =>
        {
            var entrada = Utc(r.Entrada);
            DateTimeOffset? saida = r.Saida is { } s ? Utc(s) : null;
            var fim = saida ?? agora;
            var lista = pausas[r.Id].Select(p => (Inicio: Utc(p.Inicio), Fim: p.Fim is { } f ? Utc(f) : (DateTimeOffset?)null, p)).ToList();
            var intervalos = lista.Select(p => (p.Inicio, p.Fim)).ToList();
            var dtos = lista.Select(p => new PausaDto(p.p.Id, p.Inicio, p.Fim, p.p.Motivo, PontoRegras.Minutos(p.Inicio, p.Fim ?? fim))).ToList();
            var pausado = saida is null && lista.Any(p => p.Fim is null);
            return new PontoDto(r.Id, r.PessoaId, r.PessoaNome, DateOnly.FromDateTime(r.Dia), r.Modo, PontoRegras.Estado(saida is null, pausado),
                entrada, PontoRegras.HoraLocal(entrada, fuso), saida, saida is { } x ? PontoRegras.HoraLocal(x, fuso) : null, r.FechadoPor,
                r.MotivoSaida, PontoRegras.MinutosTrabalhados(entrada, saida, intervalos, agora), PontoRegras.MinutosPausa(intervalos, fim), dtos,
                r.TurnoSituacao, r.TurnoAviso);
        }).ToList();
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
