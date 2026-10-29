using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Acontecimentos;

/// <summary>
/// Contrato que os modulos (Caixa, Compras, Estoque, Equipe, Despesas, Pedidos) usam para publicar e ler acontecimentos.
/// Toda escrita e idempotente pela chave e roda numa transacao; nada e atualizado ou apagado.
/// </summary>
public interface IAcontecimentosService
{
    Task<AcontecimentoPublicado> Publicar(Guid loja, int? responsavelId, PublicarAcontecimentoRequest request, CancellationToken ct);
    /// <summary>Publica dentro da transacao do modulo (ex.: a conclusao do pedido). Quem chama faz o commit; sem evento.</summary>
    Task<AcontecimentoPublicado> Publicar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int? responsavelId,
        PublicarAcontecimentoRequest request, CancellationToken ct);
    Task<AcontecimentoPublicado> Estornar(Guid loja, int? responsavelId, Guid acontecimentoId, EstornoRequest request, CancellationToken ct);
    Task<AcontecimentoPublicado> Ajustar(Guid loja, int? responsavelId, AjusteRequest request, CancellationToken ct);
    Task<AcontecimentoPublicado> Liquidar(Guid loja, int? responsavelId, LiquidarRequest request, CancellationToken ct);
    Task<AcontecimentoPublicado> PagarFatura(Guid loja, int? responsavelId, Guid cartaoId, DateOnly faturaMes, LiquidarRequest request,
        CancellationToken ct);
    Task<AcontecimentoPublicado> FecharMes(Guid loja, int? responsavelId, DateOnly mes, FecharMesRequest request, CancellationToken ct);
    Task<IReadOnlyList<DateOnly>> MesesFechados(Guid loja, CancellationToken ct);
    Task<AcontecimentoDto?> Obter(Guid loja, Guid id, CancellationToken ct);
    Task<IReadOnlyList<AcontecimentoDto>> LinhaDoTempo(Guid loja, PeriodoFiltro filtro, CancellationToken ct);
    Task<IReadOnlyList<LancamentoDto>> Lancamentos(Guid loja, PeriodoFiltro filtro, CancellationToken ct);
    Task<IReadOnlyList<MovimentoDto>> Movimentos(Guid loja, PeriodoFiltro filtro, CancellationToken ct);
    Task<IReadOnlyList<ContaDto>> Contas(Guid loja, CancellationToken ct);
    Task<ContaDto> CriarConta(Guid loja, int? responsavelId, ContaRequest request, CancellationToken ct);
    Task<TimeZoneInfo> Fuso(Guid loja, CancellationToken ct);
}

public sealed class AcontecimentosService(NpgsqlDataSource dataSource, IEnumerable<IAcontecimentoAssinante> assinantes,
    ILogger<AcontecimentosService> logger, TimeProvider? relogio = null) : IAcontecimentosService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    private DateTimeOffset Agora => _relogio.GetUtcNow();

    public Task<AcontecimentoPublicado> Publicar(Guid loja, int? responsavelId, PublicarAcontecimentoRequest request, CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, async (c, tx, fuso) =>
        {
            var contas = await AcontecimentoSql.ContasInfo(c, tx, loja);
            return AcontecimentoRegras.PlanejarPublicacao(request, responsavelId, contas, fuso, Agora);
        });

    public async Task<AcontecimentoPublicado> Publicar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int? responsavelId,
        PublicarAcontecimentoRequest request, CancellationToken ct)
    {
        var fuso = HorarioRegras.ResolverFuso(await AcontecimentoSql.Fuso(c, tx, loja));
        var (resultado, _) = await EscreverNaTransacao(c, tx, loja, request?.ChaveIdempotencia, fuso, async () =>
            AcontecimentoRegras.PlanejarPublicacao(request, responsavelId, await AcontecimentoSql.ContasInfo(c, tx, loja), fuso, Agora));
        return resultado;
    }

    public Task<AcontecimentoPublicado> Estornar(Guid loja, int? responsavelId, Guid acontecimentoId, EstornoRequest request,
        CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, async (c, tx, fuso) =>
        {
            await AcontecimentoSql.TravarAlvo(c, tx, loja, acontecimentoId);
            var original = await AcontecimentoSql.Atual(c, tx, loja, acontecimentoId) ?? throw NaoEncontrado("Acontecimento");
            return AcontecimentoRegras.PlanejarEstorno(original,
                await AcontecimentoSql.LancamentosAtuais(c, tx, loja, acontecimentoId),
                await AcontecimentoSql.MovimentosAtuais(c, tx, loja, acontecimentoId, null),
                await AcontecimentoSql.LiquidacoesAtuais(c, tx, loja, acontecimentoId),
                request, responsavelId, fuso, Agora);
        });

    public Task<AcontecimentoPublicado> Ajustar(Guid loja, int? responsavelId, AjusteRequest request, CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, async (c, tx, fuso) =>
        {
            if (request is null || (request.LancamentoId.HasValue == request.MovimentoId.HasValue))
                throw new GestaoException("CAMPO_INVALIDO", "Ajuste um lançamento ou um movimento de estoque.", 422);
            if (request.LancamentoId is { } lancamentoId)
            {
                var linhas = await AcontecimentoSql.LancamentoECorrecoes(c, tx, loja, lancamentoId);
                var alvo = linhas.SingleOrDefault(l => l.Id == lancamentoId) ?? throw NaoEncontrado("Lançamento");
                await AcontecimentoSql.TravarAlvo(c, tx, loja, alvo.AcontecimentoId);
                linhas = await AcontecimentoSql.LancamentoECorrecoes(c, tx, loja, lancamentoId);
                var original = await AcontecimentoSql.Atual(c, tx, loja, alvo.AcontecimentoId) ?? throw NaoEncontrado("Acontecimento");
                return AcontecimentoRegras.PlanejarAjusteLancamento(original, alvo,
                    linhas.Where(l => l.CorrigeLancamentoId == lancamentoId).ToList(), request, responsavelId, fuso, Agora);
            }
            var movimentoId = request.MovimentoId!.Value;
            var movs = await AcontecimentoSql.MovimentosAtuais(c, tx, loja, null, movimentoId);
            var mov = movs.SingleOrDefault(m => m.Id == movimentoId) ?? throw NaoEncontrado("Movimento");
            await AcontecimentoSql.TravarAlvo(c, tx, loja, mov.AcontecimentoId);
            movs = await AcontecimentoSql.MovimentosAtuais(c, tx, loja, null, movimentoId);
            var dono = await AcontecimentoSql.Atual(c, tx, loja, mov.AcontecimentoId) ?? throw NaoEncontrado("Acontecimento");
            return AcontecimentoRegras.PlanejarAjusteMovimento(dono, mov,
                movs.Where(m => m.CorrigeMovimentoId == movimentoId).ToList(), request, responsavelId, fuso, Agora);
        });

    public Task<AcontecimentoPublicado> Liquidar(Guid loja, int? responsavelId, LiquidarRequest request, CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, async (c, tx, fuso) =>
        {
            if (request is null) throw new GestaoException("CAMPO_INVALIDO", "Envie o pagamento.", 422);
            var linhas = await AcontecimentoSql.LancamentoECorrecoes(c, tx, loja, request.LancamentoId);
            var alvo = linhas.SingleOrDefault(l => l.Id == request.LancamentoId) ?? throw NaoEncontrado("Lançamento");
            await AcontecimentoSql.TravarAlvo(c, tx, loja, alvo.AcontecimentoId);
            linhas = await AcontecimentoSql.LancamentoECorrecoes(c, tx, loja, request.LancamentoId);
            alvo = linhas.Single(l => l.Id == request.LancamentoId);
            var contas = await AcontecimentoSql.ContasInfo(c, tx, loja);
            var conta = contas.GetValueOrDefault(request.ContaId) ?? throw NaoEncontrado("Conta");
            var original = await AcontecimentoSql.Atual(c, tx, loja, alvo.AcontecimentoId) ?? throw NaoEncontrado("Acontecimento");
            return AcontecimentoRegras.PlanejarLiquidacao(alvo, linhas.Where(l => l.CorrigeLancamentoId == alvo.Id).ToList(), conta,
                alvo.ContaId is { } contaAlvo ? contas.GetValueOrDefault(contaAlvo) : null, original, request, responsavelId, fuso, Agora);
        });

    public Task<AcontecimentoPublicado> PagarFatura(Guid loja, int? responsavelId, Guid cartaoId, DateOnly faturaMes,
        LiquidarRequest request, CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, async (c, tx, fuso) =>
        {
            await AcontecimentoSql.TravarAlvo(c, tx, loja, cartaoId);
            var contas = await AcontecimentoSql.ContasInfo(c, tx, loja);
            var cartao = contas.GetValueOrDefault(cartaoId) ?? throw NaoEncontrado("Cartão");
            var conta = contas.GetValueOrDefault(request?.ContaId ?? Guid.Empty) ?? throw NaoEncontrado("Conta");
            var originais = (await c.QueryAsync<Guid>(@"SELECT id FROM gestao_lancamentos WHERE estabelecimento_id = @loja
 AND conta_id = @cartaoId AND fatura_mes = @faturaMes AND corrige_lancamento_id IS NULL ORDER BY acontecimento_id, linha;",
                new { loja, cartaoId, faturaMes }, tx)).ToList();
            var itens = new List<(LancamentoAtual, IReadOnlyList<LancamentoAtual>, AcontecimentoAtual)>();
            foreach (var id in originais)
            {
                var linhas = await AcontecimentoSql.LancamentoECorrecoes(c, tx, loja, id);
                var alvo = linhas.Single(l => l.Id == id);
                var original = await AcontecimentoSql.Atual(c, tx, loja, alvo.AcontecimentoId) ?? throw NaoEncontrado("Acontecimento");
                itens.Add((alvo, linhas.Where(l => l.CorrigeLancamentoId == id).ToList(), original));
            }
            return AcontecimentoRegras.PlanejarPagamentoFatura(cartao, faturaMes, itens, conta, request, responsavelId, fuso, Agora);
        });

    public Task<AcontecimentoPublicado> FecharMes(Guid loja, int? responsavelId, DateOnly mes, FecharMesRequest request,
        CancellationToken ct) =>
        Escrever(loja, request?.ChaveIdempotencia, ct, (_, _, fuso) =>
            Task.FromResult(AcontecimentoRegras.PlanejarFechamento(mes, request, responsavelId, fuso, Agora)),
            async (c, tx, id) => await AcontecimentoSql.RegistrarFechamento(c, tx, loja, mes, id, responsavelId!.Value));

    public async Task<TimeZoneInfo> Fuso(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return HorarioRegras.ResolverFuso(await AcontecimentoSql.Fuso(c, null, loja));
    }

    public async Task<IReadOnlyList<DateOnly>> MesesFechados(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await AcontecimentoSql.MesesFechados(c, null, loja)).OrderBy(m => m).ToList();
    }

    public async Task<AcontecimentoDto?> Obter(Guid loja, Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await AcontecimentoSql.Acontecimentos(c, null, loja, new[] { id }, null, null)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<AcontecimentoDto>> LinhaDoTempo(Guid loja, PeriodoFiltro filtro, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await AcontecimentoSql.Acontecimentos(c, null, loja, null, Periodo(filtro), await AcontecimentoSql.Fuso(c, null, loja));
    }

    public async Task<IReadOnlyList<LancamentoDto>> Lancamentos(Guid loja, PeriodoFiltro filtro, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await AcontecimentoSql.Lancamentos(c, loja, Periodo(filtro), await AcontecimentoSql.Fuso(c, null, loja));
    }

    public async Task<IReadOnlyList<MovimentoDto>> Movimentos(Guid loja, PeriodoFiltro filtro, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await AcontecimentoSql.Movimentos(c, loja, Periodo(filtro), await AcontecimentoSql.Fuso(c, null, loja));
    }

    public async Task<IReadOnlyList<ContaDto>> Contas(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await AcontecimentoSql.Contas(c, loja);
    }

    public async Task<ContaDto> CriarConta(Guid loja, int? responsavelId, ContaRequest request, CancellationToken ct)
    {
        var (chave, tipo, subtipo, nome, fechamento, vencimento) = ContaRegras.Validar(request);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await AcontecimentoSql.TravarChave(c, tx, loja, "conta:" + chave);
        var id = await AcontecimentoSql.InserirConta(c, tx, loja, responsavelId, chave, tipo, subtipo, nome, fechamento, vencimento);
        await tx.CommitAsync(ct);
        return (await AcontecimentoSql.Contas(c, loja)).Single(k => k.Id == id);
    }

    // ------------------------------------------------------------------ nucleo de escrita

    private async Task<AcontecimentoPublicado> Escrever(Guid loja, string? chave, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, TimeZoneInfo, Task<AcontecimentoPlano>> planejar,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task>? depois = null)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var fuso = HorarioRegras.ResolverFuso(await AcontecimentoSql.Fuso(c, tx, loja));
        var (resultado, novo) = await EscreverNaTransacao(c, tx, loja, chave, fuso, () => planejar(c, tx, fuso), depois);
        await tx.CommitAsync(ct);
        if (novo) await Notificar(loja, resultado.Acontecimento, ct);
        return resultado;
    }

    private static async Task<(AcontecimentoPublicado, bool Novo)> EscreverNaTransacao(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid loja, string? chave, TimeZoneInfo fuso, Func<Task<AcontecimentoPlano>> planejar,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task>? depois = null)
    {
        var chaveLimpa = chave?.Trim();
        (Guid Id, string Hash)? existente = null;
        if (!string.IsNullOrEmpty(chaveLimpa))
        {
            await AcontecimentoSql.TravarChave(c, tx, loja, chaveLimpa);
            existente = await AcontecimentoSql.PorChave(c, tx, loja, chaveLimpa);
        }
        AcontecimentoPlano plano;
        try { plano = await planejar(); }
        // Replay de uma correcao ja aplicada (ja estornado, valor ja igual, ja pago): o estado mudou, a chave nao.
        catch (GestaoException) when (existente is { } e) { return (await Replay(c, tx, loja, e.Id), false); }
        if (existente is { } gravado)
        {
            if (gravado.Hash != plano.Hash)
                throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Esta chave já foi usada com outro conteúdo.", 409);
            return (await Replay(c, tx, loja, gravado.Id), false);
        }
        AcontecimentoRegras.ValidarMesesFechados(plano, await AcontecimentoSql.MesesFechados(c, tx, loja));

        Guid id;
        try
        {
            await c.ExecuteAsync("SAVEPOINT gestao_escrita;", transaction: tx);
            id = await AcontecimentoSql.Inserir(c, tx, loja, plano);
            if (depois != null) await depois(c, tx, id);
            await c.ExecuteAsync("RELEASE SAVEPOINT gestao_escrita;", transaction: tx);
        }
        catch (PostgresException ex)
        {
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT gestao_escrita;", transaction: tx);
            throw Traduzir(ex);
        }
        var dto = (await AcontecimentoSql.Acontecimentos(c, tx, loja, new[] { id }, null, null)).Single();
        return (new AcontecimentoPublicado(id, false, dto), true);
    }

    /// <summary>Mesma chave e mesmo conteudo: devolve o que ja foi gravado, sem gravar nada.</summary>
    private static async Task<AcontecimentoPublicado> Replay(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id)
    {
        var dto = (await AcontecimentoSql.Acontecimentos(c, tx, loja, new[] { id }, null, null)).Single();
        return new AcontecimentoPublicado(id, true, dto);
    }

    private static GestaoException Traduzir(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => ex.ConstraintName switch
        {
            "ux_gestao_acontecimentos_origem" => new("DUPLICADO", "Essa origem já foi registrada. Para mudar, faça um ajuste.", 409),
            "ux_gestao_acontecimentos_documento" => new("DUPLICADO", "Esse documento já foi registrado.", 409),
            "ux_gestao_acontecimentos_estorno" => new("JA_ESTORNADO", "Este acontecimento já foi estornado.", 409),
            "gestao_meses_fechados_pkey" => new("DUPLICADO", "Esse mês já está fechado.", 409),
            _ => new("DUPLICADO", "Registro repetido.", 409),
        },
        "P0002" => new("MES_FECHADO", "Mês fechado: só aceita ajuste ou estorno.", 409),
        "P0001" => new("IMUTAVEL", "Registro do livro não se altera nem se apaga.", 409),
        PostgresErrorCodes.ForeignKeyViolation => new("NAO_ENCONTRADO", "Referência não encontrada nesta loja.", 404),
        PostgresErrorCodes.CheckViolation => new("CAMPO_INVALIDO", "Dados recusados pela regra do livro.", 422),
        _ => new("ERRO_LIVRO", "Não foi possível gravar agora.", 500),
    };

    private async Task Notificar(Guid loja, AcontecimentoDto dto, CancellationToken ct)
    {
        foreach (var assinante in assinantes)
        {
            try { await assinante.AoRegistrar(new AcontecimentoRegistrado(loja, dto), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // O livro ja gravou: falha de quem escuta nao desfaz o acontecimento.
                logger.LogError(ex, "Assinante {Assinante} falhou no acontecimento {Id}.", assinante.GetType().Name, dto.Id);
            }
        }
    }

    private static PeriodoFiltro Periodo(PeriodoFiltro? filtro)
    {
        var f = filtro ?? new PeriodoFiltro();
        if (f.Mes is { } mes) f.Mes = AcontecimentoRegras.Mes(mes);
        if (f.Dia is { } dia && f.Mes is { } m && AcontecimentoRegras.Mes(dia) != m)
            throw new GestaoException("CAMPO_INVALIDO", "O dia precisa estar dentro do mês escolhido.", 422);
        if (f.Ate is { } ate && (f.Dia is not { } inicio || ate < inicio || f.Mes is { } mm && AcontecimentoRegras.Mes(ate) != mm))
            throw new GestaoException("CAMPO_INVALIDO", "O fim do intervalo precisa vir depois do início e ficar no mesmo mês.", 422);
        if (f.Mes == null && f.Dia == null && f.OrigemId == null)
            throw new GestaoException("CAMPO_INVALIDO", "Escolha o mês.", 422);
        return f;
    }

    private static GestaoException NaoEncontrado(string o) => new("NAO_ENCONTRADO", $"{o} não encontrado nesta loja.", 404);
}

/// <summary>Validacao de conta/carteira (pura).</summary>
public static class ContaRegras
{
    public static (string Chave, string Tipo, string? Subtipo, string Nome, int? Fechamento, int? Vencimento) Validar(ContaRequest? r)
    {
        if (r == null) throw Erro("Envie a conta.");
        var chave = r.ChaveIdempotencia?.Trim();
        if (string.IsNullOrEmpty(chave) || chave.Length > 150) throw Erro("Informe a chave de idempotência.");
        var tipo = r.Tipo?.Trim() ?? "";
        if (!TipoConta.Todos.Contains(tipo)) throw Erro("Tipo de conta: banco, caixa, recebivel ou cartao_empresa.");
        var nome = r.Nome?.Trim();
        if (string.IsNullOrEmpty(nome) || nome.Length > 80) throw Erro("Dê um nome à conta (até 80 caracteres).");
        var subtipo = string.IsNullOrWhiteSpace(r.Subtipo) ? null : r.Subtipo.Trim();
        if (tipo == TipoConta.Recebivel)
        {
            if (subtipo is not ("ifood" or "maquininha" or "outro")) throw Erro("Recebível: ifood, maquininha ou outro.");
        }
        else if (subtipo != null) throw Erro("Só recebível tem subtipo.");
        if (tipo == TipoConta.CartaoEmpresa)
        {
            if (r.DiaFechamento is not (>= 1 and <= 31) || r.DiaVencimento is not (>= 1 and <= 31))
                throw Erro("Cartão da empresa: informe o dia de fechamento e o de vencimento da fatura.");
            return (chave, tipo, null, nome, r.DiaFechamento, r.DiaVencimento);
        }
        if (r.DiaFechamento != null || r.DiaVencimento != null) throw Erro("Só cartão tem fechamento e vencimento.");
        return (chave, tipo, subtipo, nome, null, null);
    }

    private static GestaoException Erro(string m) => new("CAMPO_INVALIDO", m, 422);
}
