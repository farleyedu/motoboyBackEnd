using Dapper;
using Npgsql;

namespace APIBack.Assistente;

/// <summary>Estado guardado de um item da cadeia (vale para todas as instâncias do servidor).</summary>
public sealed record EstadoItem(string ItemId, string Provedor, string Modelo, DateTimeOffset? EsgotadoAte, bool Desligado, string? Erro,
    DateTimeOffset? AtualizadoEm = null, FalhaDto? Falha = null, DateTimeOffset? FalhaEm = null);

/// <summary>
/// Foto do momento: estado de cada item, quantos pedidos cada um já respondeu hoje (dia de São Paulo) e os parâmetros que cada
/// provedor já recusou (aprendido; ver <see cref="RegrasCadeia.DefinicaoEfetiva"/>).
/// </summary>
public sealed record FotoCadeia(IReadOnlyDictionary<string, EstadoItem> Estado, IReadOnlyDictionary<string, long> UsadasHoje,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? NaoSuporta = null)
{
    public static readonly FotoCadeia Vazia = new(new Dictionary<string, EstadoItem>(), new Dictionary<string, long>());
}

public static class MotivosPulo
{
    // Indisponível antes de tentar
    public const string DesligadoConfig = "desligado_config";
    public const string DesligadoErro = "desligado_erro";
    public const string Esgotado = "esgotado";
    public const string LimiteDia = "limite_dia";
    public const string SemChave = "sem_chave";
    public const string PagoBloqueado = "pago_bloqueado";
    // Falhou ao tentar
    public const string LimiteProvedor = "limite_provedor";
    public const string ErroServidor = "erro_servidor";
    public const string Desligado = "desligado";
    public const string PedidoRecusado = "pedido_recusado";

    public static string DeFalha(TipoFalha t) => t switch
    {
        TipoFalha.Limite => LimiteProvedor,
        TipoFalha.Servidor => ErroServidor,
        TipoFalha.SemChave => SemChave,
        TipoFalha.Configuracao => Desligado,
        _ => PedidoRecusado,
    };
}

public sealed record Pulo(ItemCadeia Item, string Motivo, string? Erro);

public sealed record DisponibilidadeItem(ItemCadeia Item, bool Disponivel, string? Motivo, DateTimeOffset? EsgotadoAte, bool Desligado, string? Erro,
    long UsadasHoje, bool TemChave);

/// <summary>Plano de uma pergunta: em que ordem tentar os itens disponíveis e quais ficaram de fora (e por quê).</summary>
public sealed record PlanoCadeia(IReadOnlyList<ItemCadeia> Tentar, IReadOnlyList<Pulo> Pulados);

/// <summary>Regras da cadeia, sem rede nem banco. Público para teste.</summary>
public static class RegrasCadeia
{
    public const int LimiteSemValor = 1000;
    public static readonly TimeSpan EsperaPadrao = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EsperaMinuto = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromHours(24);

    /// <summary>O estado guardado só vale se o item ainda é o mesmo provedor + modelo (mudou na Gestão = estado zerado).</summary>
    public static EstadoItem? EstadoValido(ItemCadeia item, FotoCadeia foto) =>
        foto.Estado.TryGetValue(item.Id, out var e) && e.Provedor == item.Provedor && e.Modelo == item.Modelo ? e : null;

    /// <summary>Se o item pode ser tentado agora e, se não, por quê. Ordem dos motivos: config, erro, pago, chave, esgotado, limite.</summary>
    public static DisponibilidadeItem Disponibilidade(ConfigIA cfg, ItemCadeia item, FotoCadeia foto, Func<DefinicaoProvedor, bool> temChave, DateTimeOffset agora)
    {
        var def = cfg.ProvedorDe(item.Provedor);
        var estado = EstadoValido(item, foto);
        var usadas = foto.UsadasHoje.GetValueOrDefault(item.Id);
        var chave = def is not null && temChave(def);
        var esgotadoAte = estado?.EsgotadoAte is { } ate && ate > agora ? ate : (DateTimeOffset?)null;
        string? motivo =
            !item.Ligado || def is null ? MotivosPulo.DesligadoConfig
            : estado?.Desligado == true ? MotivosPulo.DesligadoErro
            : !def.Gratis && cfg.Pagos == PoliticasPagos.NuncaPago ? MotivosPulo.PagoBloqueado
            : !chave ? MotivosPulo.SemChave
            : esgotadoAte is not null ? MotivosPulo.Esgotado
            : item.LimiteDia is int lim && usadas >= lim ? MotivosPulo.LimiteDia
            : null;
        return new DisponibilidadeItem(item, motivo is null, motivo, esgotadoAte, estado?.Desligado == true, estado?.Erro, usadas, chave);
    }

    /// <summary>
    /// Ordem de tentativa. <c>ordem</c>: a da cadeia. <c>revezar</c>: menor fração do limite do dia gasta primeiro (sem limite = 1.000;
    /// empate fica na ordem da cadeia). Com <c>pagoSoNoFim</c>, todo pago vai depois de todo grátis.
    /// </summary>
    public static PlanoCadeia Planejar(ConfigIA cfg, FotoCadeia foto, Func<DefinicaoProvedor, bool> temChave, DateTimeOffset agora)
    {
        var avaliados = cfg.Cadeia.Select((item, pos) => (Pos: pos, D: Disponibilidade(cfg, item, foto, temChave, agora))).ToList();
        var pulados = avaliados.Where(a => !a.D.Disponivel && a.D.Motivo != MotivosPulo.DesligadoConfig)
            .Select(a => new Pulo(a.D.Item, a.D.Motivo!, a.D.Erro)).ToList();
        var disponiveis = avaliados.Where(a => a.D.Disponivel);
        bool Pago((int Pos, DisponibilidadeItem D) a) => cfg.ProvedorDe(a.D.Item.Provedor)?.Gratis != true;
        double Fracao((int Pos, DisponibilidadeItem D) a) => (double)a.D.UsadasHoje / (a.D.Item.LimiteDia ?? LimiteSemValor);

        var ordenados = cfg.Modo == ModosCadeia.Revezar
            ? disponiveis.OrderBy(a => Pago(a) ? 1 : 0).ThenBy(Fracao).ThenBy(a => a.Pos)
            : disponiveis.OrderBy(a => Pago(a) ? 1 : 0).ThenBy(a => a.Pos);
        return new PlanoCadeia(ordenados.Select(a => a.D.Item).ToList(), pulados);
    }

    /// <summary>A definição com as flags que o provedor já mostrou não aceitar desligadas (o resto, como a Gestão salvou).</summary>
    public static DefinicaoProvedor DefinicaoEfetiva(DefinicaoProvedor d, FotoCadeia foto)
    {
        if (foto.NaoSuporta?.TryGetValue(d.Id, out var nao) != true || nao is null || nao.Count == 0) return d;
        return d with
        {
            AceitaStrict = d.AceitaStrict && !nao.Contains(ProvedorOpenAI.ParamStrict),
            AceitaEsforco = d.AceitaEsforco && !nao.Contains(ProvedorOpenAI.ParamEsforco),
            EsquemaCompleto = d.EsquemaCompleto && !nao.Contains(ProvedorOpenAI.ParamEsquema),
        };
    }

    /// <summary>
    /// Até quando o item fica esgotado depois de uma falha transitória: meia-noite se cota do dia (OpenRouter: 00:00 UTC); senão o
    /// retry-after; senão 1 min (limite por minuto) ou 5 min.
    /// </summary>
    public static DateTimeOffset EsgotadoAte(FalhaProvedorException f, DateTimeOffset agora)
    {
        if (f.CotaDiaria)
            return f.Provedor == Provedores.OpenRouter
                ? new DateTimeOffset(agora.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)
                : AssistenteService.InicioDoDia(AssistenteService.DiaLocal(agora).AddDays(1));
        if (f.RetryAfter is { } ra && ra > TimeSpan.Zero) return agora + (ra > EsperaMaxima ? EsperaMaxima : ra);
        return agora + (f.Codigo == CodigosFalha.LimiteMinuto ? EsperaMinuto : EsperaPadrao);
    }
}

public sealed record RespostaComProvedor(RespostaIA Resposta, string ItemId, string Provedor, string Modelo, bool Trocou, IReadOnlyList<Pulo> Pulos);

public interface IEstadoCadeiaRepositorio
{
    Task<FotoCadeia> Foto(DateTimeOffset inicioDoDiaUtc, CancellationToken ct);
    Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct);
    Task Desligar(ItemCadeia item, string erro, CancellationToken ct);
    Task Religar(string itemId, CancellationToken ct);
    Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct);
    /// <summary>Esquece o aprendido dos provedores que a Gestão mudou (URL, flags...).</summary>
    Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct);
    /// <summary>Guarda a última falha explicada do item (CONTRATO-API §12.6). Sem a migração 20261209_01, não faz nada.</summary>
    Task RegistrarFalha(ItemCadeia item, FalhaDto falha, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Roda uma pergunta na cadeia: planeja, tenta item por item e, em falha, passa ao próximo na mesma pergunta, marcando
/// "esgotado até" ou desligando o item no banco. Os pulos voltam na resposta (o serviço grava para o uso da Gestão).
/// </summary>
public sealed class CadeiaIA(IFabricaProvedores fabrica, IEstadoCadeiaRepositorio estado, TimeProvider relogio, ILogger<CadeiaIA> logger,
    ChavesIA? chaves = null)
{
    public bool TemChave(DefinicaoProvedor d) => fabrica.TemChave(d);

    /// <summary>Recarrega as chaves do banco se passou 1 minuto (antes de decidir se o assistente está disponível).</summary>
    public Task PrepararChaves(CancellationToken ct) => chaves?.Garantir(ct) ?? Task.CompletedTask;

    public async Task<FotoCadeia> Foto(CancellationToken ct)
    {
        if (chaves is not null) await chaves.Garantir(ct);
        try { return await estado.Foto(AssistenteService.InicioDoDia(relogio.GetUtcNow()), ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable || ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            logger.LogWarning("Assistente: estado da cadeia indisponível (migrações 20261205_07/09 pendentes); seguindo sem estado.");
            return FotoCadeia.Vazia;
        }
    }

    /// <summary>Ligado e com algum item que pode ser tentado (ignora esgotado e limite: isso dá 502, não 503).</summary>
    public bool Disponivel(ConfigIA cfg) => cfg.Ligado && cfg.Cadeia.Any(i =>
    {
        var d = RegrasCadeia.Disponibilidade(cfg, i, FotoCadeia.Vazia, fabrica.TemChave, relogio.GetUtcNow());
        return d.Disponivel;
    });

    /// <summary>Pergunta na cadeia inteira (o chat). <paramref name="antesDeTrocar"/> limpa o que o item que caiu deixou pela metade.</summary>
    public async Task<RespostaComProvedor> Responder(ConfigIA cfg, Func<ItemCadeia, PedidoIA> montar, ExecutarFerramenta executar,
        Action? antesDeTrocar, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow();
        var foto = await Foto(ct);
        var plano = RegrasCadeia.Planejar(cfg, foto, fabrica.TemChave, agora);
        var pulos = new List<Pulo>(plano.Pulados);
        var falhas = new List<string>();
        (ItemCadeia Item, FalhaProvedorException Falha)? ultima = null;
        foreach (var item in plano.Tentar)
        {
            if (falhas.Count > 0) antesDeTrocar?.Invoke();
            try
            {
                var r = await Tentar(cfg, item, foto, montar, executar, ct);
                return new RespostaComProvedor(r, item.Id, item.Provedor, item.Modelo, falhas.Count > 0, pulos);
            }
            catch (FalhaProvedorException f)
            {
                // Log com a mensagem do provedor já limpa (sem chave, cortada); nunca a pergunta nem a resposta.
                logger.LogWarning("Assistente: item {Item} ({Provedor}/{Modelo}) falhou: {Codigo} {Status} {CodigoProvedor} {Mensagem}",
                    item.Id, item.Provedor, item.Modelo, f.Codigo, f.Status, f.CodigoProvedor, f.MensagemProvedor);
                falhas.Add($"{item.Id}: {f.Message}");
                pulos.Add(new Pulo(item, MotivosPulo.DeFalha(f.Tipo), f.Message));
                ultima = (item, f);
                await Registrar(cfg, item, f, ct);
            }
        }
        var resumo = falhas.Count > 0 ? string.Join(" ", falhas)
            : plano.Pulados.Count > 0 ? "Nenhuma IA da cadeia está disponível agora." : "A cadeia está vazia.";
        throw new FalhaCadeiaException(resumo, pulos) { Ultima = ultima };
    }

    /// <summary>Um item só, sem troca e sem olhar estado (o testar da Gestão).</summary>
    public async Task<RespostaComProvedor> ResponderCom(ConfigIA cfg, ItemCadeia item, Func<ItemCadeia, PedidoIA> montar, ExecutarFerramenta executar, CancellationToken ct)
    {
        if (cfg.ProvedorDe(item.Provedor) is null) throw new FalhaProvedorException(item.Provedor, $"Provedor {item.Provedor} não cadastrado.", TipoFalha.Configuracao);
        if (string.IsNullOrWhiteSpace(item.Modelo)) throw new FalhaProvedorException(item.Provedor, "Item sem modelo.", TipoFalha.Configuracao);
        var r = await Tentar(cfg, item, await Foto(ct), montar, executar, ct);
        return new RespostaComProvedor(r, item.Id, item.Provedor, item.Modelo, false, []);
    }

    /// <summary>Chama o provedor do item com as flags efetivas e grava o que ele mostrou não aceitar.</summary>
    private async Task<RespostaIA> Tentar(ConfigIA cfg, ItemCadeia item, FotoCadeia foto, Func<ItemCadeia, PedidoIA> montar, ExecutarFerramenta executar, CancellationToken ct)
    {
        var def = RegrasCadeia.DefinicaoEfetiva(cfg.ProvedorDe(item.Provedor)!, foto);
        var aprendidos = new List<string>();
        try
        {
            return await fabrica.Criar(def).Responder(montar(item) with { NaoSuporta = aprendidos.Add }, executar, ct);
        }
        finally
        {
            foreach (var p in aprendidos.Distinct())
            {
                try { await estado.RegistrarNaoSuporta(def.Id, p, CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Assistente: não foi possível gravar que {Provedor} não aceita {Parametro} ({Erro})", def.Id, p, ex.GetType().Name);
                }
            }
        }
    }

    private async Task Registrar(ConfigIA cfg, ItemCadeia item, FalhaProvedorException f, CancellationToken ct)
    {
        try
        {
            if (f.Tipo is TipoFalha.Limite or TipoFalha.Servidor) await estado.MarcarEsgotado(item, RegrasCadeia.EsgotadoAte(f, relogio.GetUtcNow()), f.Message, CancellationToken.None);
            else if (f.Tipo == TipoFalha.Configuracao) await estado.Desligar(item, f.Message, CancellationToken.None);
            var def = cfg.ProvedorDe(item.Provedor);
            var origem = def is null ? null : fabrica.Descrever(def).Origem;
            await estado.RegistrarFalha(item, CatalogoFalhas.Dto(f, def?.Nome, item.Modelo, relogio.GetUtcNow(), origem) with { Etapa = f.Etapa ?? EtapasFalha.ChamadaModelo },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Assistente: não foi possível gravar o estado do item {Item} ({Erro})", item.Id, ex.GetType().Name);
        }
    }
}

/// <summary>Nenhum item da cadeia respondeu. Leva os pulos para o serviço gravar.</summary>
public sealed class FalhaCadeiaException(string mensagem, IReadOnlyList<Pulo> pulos) : Exception(mensagem)
{
    public IReadOnlyList<Pulo> Pulos { get; } = pulos;
    /// <summary>A última falha de quem tentou (para o teste da Gestão explicar), ou nulo se ninguém pôde tentar.</summary>
    public (ItemCadeia Item, FalhaProvedorException Falha)? Ultima { get; init; }
}

public sealed class EstadoCadeiaRepositorio(NpgsqlDataSource source) : IEstadoCadeiaRepositorio
{
    public async Task<FotoCadeia> Foto(DateTimeOffset inicioDoDia, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        static DateTimeOffset? Utc(DateTime? d) => d is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
        Dictionary<string, EstadoItem> estados;
        try
        {
            estados = (await c.QueryAsync<(string ItemId, string Provedor, string Modelo, DateTime? EsgotadoAte, bool Desligado, string? Erro, DateTime AtualizadoEm, string? Falha, DateTime? FalhaEm)>(new CommandDefinition(
                "SELECT item_id, provedor, modelo, esgotado_ate, desligado, erro, atualizado_em, falha::text, falha_em FROM assistente_cadeia_estado", cancellationToken: ct)))
                .ToDictionary(e => e.ItemId, e => new EstadoItem(e.ItemId, e.Provedor, e.Modelo, Utc(e.EsgotadoAte), e.Desligado, e.Erro, Utc(e.AtualizadoEm), LerFalha(e.Falha), Utc(e.FalhaEm)));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            // Migração 20261209_01 pendente: sem a última falha explicada.
            estados = (await c.QueryAsync<(string ItemId, string Provedor, string Modelo, DateTime? EsgotadoAte, bool Desligado, string? Erro, DateTime AtualizadoEm)>(new CommandDefinition(
                "SELECT item_id, provedor, modelo, esgotado_ate, desligado, erro, atualizado_em FROM assistente_cadeia_estado", cancellationToken: ct)))
                .ToDictionary(e => e.ItemId, e => new EstadoItem(e.ItemId, e.Provedor, e.Modelo, Utc(e.EsgotadoAte), e.Desligado, e.Erro, Utc(e.AtualizadoEm)));
        }
        var usadas = (await c.QueryAsync<(string ItemId, long N)>(new CommandDefinition(
            "SELECT item_id, COUNT(*) FROM assistente_uso WHERE item_id IS NOT NULL AND criado_em >= @inicioDoDia GROUP BY 1",
            new { inicioDoDia }, cancellationToken: ct))).ToDictionary(r => r.ItemId, r => r.N);
        IReadOnlyDictionary<string, IReadOnlySet<string>> naoSuporta = new Dictionary<string, IReadOnlySet<string>>();
        try
        {
            naoSuporta = (await c.QueryAsync<(string Provedor, string Parametro)>(new CommandDefinition(
                    "SELECT provedor, parametro FROM assistente_provedor_nao_suporta", cancellationToken: ct)))
                .GroupBy(r => r.Provedor).ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(r => r.Parametro).ToHashSet());
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* migração 20261205_09 pendente */ }
        return new FotoCadeia(estados, usadas, naoSuporta);
    }

    public async Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO assistente_provedor_nao_suporta (provedor, parametro) VALUES (@provedor, @parametro) ON CONFLICT DO NOTHING",
            new { provedor, parametro }, cancellationToken: ct));
    }

    public async Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct)
    {
        if (provedores.Count == 0) return;
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM assistente_provedor_nao_suporta WHERE provedor = ANY(@ids)", new { ids = provedores.ToArray() }, cancellationToken: ct));
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static FalhaDto? LerFalha(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<FalhaDto>(json, Json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public async Task RegistrarFalha(ItemCadeia item, FalhaDto falha, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO assistente_cadeia_estado (item_id, provedor, modelo, falha, falha_em, atualizado_em)
                VALUES (@Id, @Provedor, @Modelo, CAST(@falha AS JSONB), NOW(), NOW())
                ON CONFLICT (item_id) DO UPDATE SET
                    -- Mudou o item (provedor ou modelo): o estado antigo não vale mais.
                    esgotado_ate = CASE WHEN assistente_cadeia_estado.provedor = EXCLUDED.provedor AND assistente_cadeia_estado.modelo = EXCLUDED.modelo
                                        THEN assistente_cadeia_estado.esgotado_ate ELSE NULL END,
                    desligado = CASE WHEN assistente_cadeia_estado.provedor = EXCLUDED.provedor AND assistente_cadeia_estado.modelo = EXCLUDED.modelo
                                     THEN assistente_cadeia_estado.desligado ELSE FALSE END,
                    provedor = EXCLUDED.provedor, modelo = EXCLUDED.modelo, falha = EXCLUDED.falha, falha_em = NOW()
                """, new { item.Id, item.Provedor, item.Modelo, falha = System.Text.Json.JsonSerializer.Serialize(falha, Json) }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn) { /* migração 20261209_01 pendente */ }
    }

    public Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct) => Gravar(item, ate, false, erro, ct);

    public Task Desligar(ItemCadeia item, string erro, CancellationToken ct) => Gravar(item, null, true, erro, ct);

    private async Task Gravar(ItemCadeia item, DateTimeOffset? ate, bool desligar, string erro, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_cadeia_estado (item_id, provedor, modelo, esgotado_ate, desligado, erro, atualizado_em)
            VALUES (@Id, @Provedor, @Modelo, @ate, @desligar, @erro, NOW())
            ON CONFLICT (item_id) DO UPDATE SET
                provedor = EXCLUDED.provedor, modelo = EXCLUDED.modelo,
                -- Mesmo provedor + modelo: soma ao estado (desligar não apaga o esgotado e vice-versa). Mudou o item: começa do zero.
                esgotado_ate = CASE WHEN @desligar AND assistente_cadeia_estado.provedor = EXCLUDED.provedor AND assistente_cadeia_estado.modelo = EXCLUDED.modelo
                                    THEN assistente_cadeia_estado.esgotado_ate ELSE EXCLUDED.esgotado_ate END,
                desligado = CASE WHEN assistente_cadeia_estado.provedor = EXCLUDED.provedor AND assistente_cadeia_estado.modelo = EXCLUDED.modelo
                                 THEN assistente_cadeia_estado.desligado OR EXCLUDED.desligado ELSE EXCLUDED.desligado END,
                erro = EXCLUDED.erro, atualizado_em = NOW()
            """, new { item.Id, item.Provedor, item.Modelo, ate, desligar, erro }, cancellationToken: ct));
    }

    public async Task Religar(string itemId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM assistente_cadeia_estado WHERE item_id = @itemId", new { itemId }, cancellationToken: ct));
    }
}

/// <summary>Um aviso para a Gestão (CONTRATO-API §10.3).</summary>
public sealed record AlertaDto(string Tipo, string Mensagem, DateTimeOffset? Desde, string? ItemId, string? Codigo = null, IReadOnlyList<string>? Acoes = null);

/// <summary>Alertas da cadeia, sem banco. Público para teste.</summary>
public static class AlertasCadeia
{
    public const string ItemDesligado = "item_desligado";
    public const string GratisEsgotados = "gratis_esgotados";
    public const string CadeiaEsgotada = "cadeia_esgotada";
    public const string SemChave = "sem_chave";

    private static readonly string[] MotivosGratisAcabou = [MotivosPulo.Esgotado, MotivosPulo.LimiteDia, MotivosPulo.DesligadoErro, MotivosPulo.SemChave];

    public static IReadOnlyList<AlertaDto> Calcular(ConfigIA cfg, FotoCadeia foto, Func<DefinicaoProvedor, bool> temChave, DateTimeOffset agora,
        long esgotadaHoje, DateTimeOffset? primeiraEsgotadaHoje)
    {
        var alertas = new List<AlertaDto>();
        var itens = cfg.Cadeia.Where(i => i.Ligado).Select(i => (Item: i, D: RegrasCadeia.Disponibilidade(cfg, i, foto, temChave, agora),
            Estado: RegrasCadeia.EstadoValido(i, foto), Def: cfg.ProvedorDe(i.Provedor))).ToList();
        string Nome((ItemCadeia Item, DisponibilidadeItem D, EstadoItem? Estado, DefinicaoProvedor? Def) x) => x.Def?.Nome ?? x.Item.Provedor;

        foreach (var x in itens.Where(x => x.D.Motivo == MotivosPulo.DesligadoErro))
            alertas.Add(new AlertaDto(ItemDesligado, $"{Nome(x)} ({x.Item.Modelo}) desligado: {x.Estado?.Erro ?? "erro"}.", x.Estado?.AtualizadoEm, x.Item.Id));

        var gratis = itens.Where(x => x.Def?.Gratis == true).ToList();
        if (gratis.Count > 0 && gratis.All(x => !x.D.Disponivel && MotivosGratisAcabou.Contains(x.D.Motivo)))
        {
            var temPago = itens.Any(x => x.Def?.Gratis != true && x.D.Disponivel);
            var desde = gratis.Select(x => x.Estado?.AtualizadoEm).Where(d => d is not null).DefaultIfEmpty(null).Max();
            alertas.Add(new AlertaDto(GratisEsgotados,
                temPago ? "Todas as IAs grátis acabaram por hoje; a cadeia está no pago." : "Todas as IAs grátis acabaram por hoje.", desde, null));
        }

        if (esgotadaHoje > 0)
            alertas.Add(new AlertaDto(CadeiaEsgotada,
                esgotadaHoje == 1 ? "1 pergunta ficou sem IA hoje (todas esgotadas ou com erro)." : $"{esgotadaHoje} perguntas ficaram sem IA hoje (todas esgotadas ou com erro).",
                primeiraEsgotadaHoje, null));

        foreach (var x in itens.Where(x => x.D.Motivo == MotivosPulo.SemChave))
            alertas.Add(new AlertaDto(SemChave, string.IsNullOrWhiteSpace(x.Def?.VariavelChave)
                ? $"{Nome(x)} sem chave: cadastre na Gestão."
                : $"{Nome(x)} sem chave: cadastre na Gestão ou em {x.Def!.VariavelChave}.", null, x.Item.Id));
        return alertas;
    }

    /// <summary>Provedores cuja definição mudou ao salvar (o aprendido deles é esquecido). Público para teste.</summary>
    public static IReadOnlyList<string> ProvedoresMudados(ConfigIA antes, ConfigIA depois) => depois.Definicoes
        .Where(d => antes.ProvedorDe(d.Id) is not { } a || !MesmaDefinicao(a, d)).Select(d => d.Id).ToList();

    private static bool MesmaDefinicao(DefinicaoProvedor a, DefinicaoProvedor b) =>
        a.Tipo == b.Tipo && a.UrlBase == b.UrlBase && a.AceitaStrict == b.AceitaStrict && a.AceitaEsforco == b.AceitaEsforco && a.EsquemaCompleto == b.EsquemaCompleto;
}
