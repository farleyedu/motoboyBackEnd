using System.Diagnostics;

namespace APIBack.Assistente;

// Diagnóstico das IAs pela Gestão (CONTRATO-API §12.4, §12.5 e §12.7). A chave colada nunca é gravada nem registrada aqui.

public sealed record EtapaDiagnosticoDto(string Etapa, string Situacao, long? Ms, string Texto, FalhaDto? Falha);

public sealed record DiagnosticoDto(string Provedor, string? Modelo, string OrigemChave, bool Ok, IReadOnlyList<EtapaDiagnosticoDto> Etapas, FalhaDto? Falha,
    bool? Religado = null, bool ListaPublica = false);

public sealed record DiagnosticoItemDto(string ItemId, string Provedor, string? Modelo, bool Ok, FalhaDto? Falha, IReadOnlyList<EtapaDiagnosticoDto> Etapas, bool? Religado);

public sealed record VerificacaoDto(IReadOnlyList<DiagnosticoItemDto> Itens);

public sealed record ListaModelosDto(string Provedor, bool Ok, IReadOnlyList<ModeloInfoDto> Modelos, FalhaDto? Falha, bool ListaPublica);

public sealed class DiagnosticarRequest
{
    public string? Modelo { get; set; }
    public string? Chave { get; set; }
    public bool Ferramentas { get; set; }
}

public sealed class ListarModelosRequest
{
    public string? Chave { get; set; }
}

public static class SituacoesEtapa
{
    public const string Ok = "ok";
    public const string Aviso = "aviso";
    public const string Falhou = "falhou";
    public const string Pulada = "pulada";
}

/// <summary>Roda as etapas em ordem e para na primeira que falhar. Público para teste.</summary>
public sealed class DiagnosticoIA(ICriadorProvedores criador, IEstadoCadeiaRepositorio estado, TimeProvider relogio, ILogger<DiagnosticoIA> logger,
    Func<ConfigIA, string, CancellationToken, Task<TesteIADto>>? checarFerramentas = null)
{
    public static readonly TimeSpan TempoLista = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TempoChamada = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan TempoItem = TimeSpan.FromSeconds(25);
    public const int MinTokensTeste = 1024;
    public const int MaxParalelo = 4;
    public const int MinTamanho = 8;
    public const int MaxTamanho = 500;

    /// <summary>Tira o que costuma vir junto ao copiar: aspas, "Bearer ", espaços nas pontas.</summary>
    public static string Limpar(string? chave)
    {
        var v = (chave ?? "").Trim().Trim('"', '\'', '`').Trim();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v["Bearer ".Length..].Trim();
        return v;
    }

    /// <summary>Problema de formato da chave (já limpa), ou nulo.</summary>
    public static string? ProblemaFormato(string chave)
    {
        if (chave.Length == 0) return "A chave está vazia.";
        if (chave.Length < MinTamanho) return "A chave está curta demais: parece que faltou um pedaço.";
        if (chave.Length > MaxTamanho) return "A chave está longa demais: parece que veio texto junto.";
        if (chave.Any(char.IsWhiteSpace)) return "A chave tem espaço ou quebra de linha no meio. Copie de novo, inteira.";
        if (chave.Any(c => c is '"' or '\'' or '`' or '<' or '>')) return "A chave tem aspas ou sinais que não fazem parte dela.";
        return null;
    }

    public async Task<ListaModelosDto> ListarModelos(ConfigIA cfg, string provedorId, string? chaveColada, CancellationToken ct)
    {
        var def = Definicao(cfg, provedorId);
        var publica = def.Id == Provedores.OpenRouter;
        var (valor, origem) = Chave(def, chaveColada);
        if (valor is null)
            return new(def.Id, false, [], CatalogoFalhas.Dto(CodigosFalha.SemChave, def.Id, def.Nome, null, EtapasFalha.FormatoChave), publica);
        if (chaveColada is not null && ProblemaFormato(valor) is string p)
            return new(def.Id, false, [], CatalogoFalhas.Dto(CodigosFalha.ChaveFormato, def.Id, def.Nome, null, EtapasFalha.FormatoChave, p), publica);
        try
        {
            var lista = await LimitesChamada.ComTempo(def.Id, c => criador.CriarCom(def, valor).ModelosTodos(c), ct, TempoLista);
            return new(def.Id, true, CatalogoModelos.Montar(def.Id, lista), null, publica);
        }
        catch (FalhaProvedorException f)
        {
            f.Etapa ??= EtapasFalha.ListaModelos;
            Logar(def.Id, null, f);
            return new(def.Id, false, [], CatalogoFalhas.Dto(f, def.Nome, null, relogio.GetUtcNow(), origem), publica);
        }
    }

    /// <summary>
    /// Etapas: formato da chave → lista de modelos → modelo na lista → chamada ao modelo → ferramentas. Sem <paramref name="chaveColada"/>
    /// usa a chave em uso. Com tudo ok e a chave em uso, religa os itens desse provedor + modelo que estavam desligados por erro.
    /// </summary>
    public async Task<DiagnosticoDto> Diagnosticar(ConfigIA cfg, string provedorId, string? modelo, string? chaveColada, bool ferramentas, CancellationToken ct)
    {
        var def = Definicao(cfg, provedorId);
        modelo = string.IsNullOrWhiteSpace(modelo) ? null : CatalogoModelos.Normalizar(modelo.Trim());
        var etapas = new List<EtapaDiagnosticoDto>();
        var publica = def.Id == Provedores.OpenRouter;
        var agora = relogio.GetUtcNow();
        var (valor, origem) = Chave(def, chaveColada);
        var origemDto = chaveColada is null ? origem : "colada";

        DiagnosticoDto Parar(FalhaDto falha)
        {
            var feitas = etapas.Select(e => e.Etapa).ToHashSet();
            foreach (var (e, t) in Ordem.Where(o => !feitas.Contains(o.Etapa)))
                etapas.Add(new EtapaDiagnosticoDto(e, SituacoesEtapa.Pulada, null, t, null));
            return new DiagnosticoDto(def.Id, modelo, origemDto, false, etapas, falha, null, publica);
        }

        // 1. formato da chave
        if (valor is null)
        {
            var f = CatalogoFalhas.Dto(CodigosFalha.SemChave, def.Id, def.Nome, modelo, EtapasFalha.FormatoChave);
            etapas.Add(new(EtapasFalha.FormatoChave, SituacoesEtapa.Falhou, 0, "Nenhuma chave cadastrada", f));
            return Parar(f);
        }
        if (chaveColada is not null)
        {
            if (ProblemaFormato(valor) is string p)
            {
                var f = CatalogoFalhas.Dto(CodigosFalha.ChaveFormato, def.Id, def.Nome, modelo, EtapasFalha.FormatoChave, p);
                etapas.Add(new(EtapasFalha.FormatoChave, SituacoesEtapa.Falhou, 0, "A chave não parece certa", f));
                return Parar(f);
            }
            var de = CatalogoModelos.ProvedorDaChave(valor);
            // Provedor criado na tela pode aceitar qualquer formato: só avisa nos conhecidos.
            if (de is not null && de != def.Id && Conhecido(def.Id))
            {
                var nomeOutro = cfg.ProvedorDe(de)?.Nome ?? de;
                etapas.Add(new(EtapasFalha.FormatoChave, SituacoesEtapa.Aviso, 0, $"Parece chave de {nomeOutro}",
                    CatalogoFalhas.Dto(CodigosFalha.ChaveOutroProvedor, def.Id, def.Nome, modelo, EtapasFalha.FormatoChave,
                        $"O começo da chave lembra {nomeOutro}, não {def.Nome}. Vamos testar mesmo assim.")));
            }
            else etapas.Add(new(EtapasFalha.FormatoChave, SituacoesEtapa.Ok, 0, "Formato da chave confere", null));
        }
        else etapas.Add(new(EtapasFalha.FormatoChave, SituacoesEtapa.Ok, 0, origem == OrigensChave.Ambiente
            ? $"Usando a chave da variável {def.VariavelChave} do servidor (final {CifraChavesAssistente.Final(valor)})"
            : $"Usando a chave salva (final {CifraChavesAssistente.Final(valor)})", null));

        var provedor = criador.CriarCom(def, valor);

        // 2. lista de modelos
        IReadOnlyList<string> lista;
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            lista = await LimitesChamada.ComTempo(def.Id, c => provedor.ModelosTodos(c), ct, TempoLista);
            etapas.Add(publica
                ? new(EtapasFalha.ListaModelos, SituacoesEtapa.Aviso, Ms(t0), $"Lista pública ({lista.Count} modelos): não prova a chave; a chamada prova", null)
                : new(EtapasFalha.ListaModelos, SituacoesEtapa.Ok, Ms(t0), $"A chave foi aceita ({lista.Count} modelos)", null));
        }
        catch (FalhaProvedorException f)
        {
            f.Etapa ??= EtapasFalha.ListaModelos;
            Logar(def.Id, modelo, f);
            var dto = CatalogoFalhas.Dto(f, def.Nome, modelo, agora, chaveColada is null ? origem : null);
            etapas.Add(new(EtapasFalha.ListaModelos, SituacoesEtapa.Falhou, Ms(t0), "A lista de modelos não veio", dto));
            return Parar(dto);
        }
        var sugeridos = CatalogoModelos.Sugeridos(def.Id, lista, modelo);

        // 3. modelo na lista
        if (modelo is null)
        {
            etapas.Add(new(EtapasFalha.ModeloNaLista, SituacoesEtapa.Pulada, null, "Nenhum modelo escolhido", null));
            etapas.Add(new(EtapasFalha.ChamadaModelo, SituacoesEtapa.Pulada, null, "Nenhum modelo escolhido", null));
            etapas.Add(new(EtapasFalha.Ferramentas, SituacoesEtapa.Pulada, null, "Nenhum modelo escolhido", null));
            return new DiagnosticoDto(def.Id, null, origemDto, true, etapas, null, null, publica);
        }
        var normalizados = lista.Select(CatalogoModelos.Normalizar).ToHashSet();
        if (lista.Count > 0 && !normalizados.Contains(modelo))
        {
            var f = CatalogoFalhas.Dto(CatalogoFalhas.Nova(def.Id, CodigosFalha.ModeloNaoExiste, etapa: EtapasFalha.ModeloNaLista), def.Nome, modelo, agora, null, sugeridos);
            etapas.Add(new(EtapasFalha.ModeloNaLista, SituacoesEtapa.Falhou, 0, $"{modelo} não está na lista desta chave", f));
            return Parar(f);
        }
        if (CatalogoModelos.Tipo(modelo) != CatalogoModelos.Conversa)
        {
            var f = CatalogoFalhas.Dto(CatalogoFalhas.Nova(def.Id, CodigosFalha.ModeloNaoConversa, etapa: EtapasFalha.ModeloNaLista), def.Nome, modelo, agora, null, sugeridos);
            etapas.Add(new(EtapasFalha.ModeloNaLista, SituacoesEtapa.Falhou, 0, $"{modelo} não é de conversa", f));
            return Parar(f);
        }
        etapas.Add(lista.Count == 0
            ? new(EtapasFalha.ModeloNaLista, SituacoesEtapa.Aviso, 0, "A lista veio vazia; seguindo para a chamada", null)
            : new(EtapasFalha.ModeloNaLista, SituacoesEtapa.Ok, 0, $"{modelo} existe e é de conversa", null));

        // 4. chamada ao modelo
        t0 = Stopwatch.GetTimestamp();
        try
        {
            var tokens = Math.Clamp(cfg.MaxTokens, MinTokensTeste, 32000);
            var cortada = await LimitesChamada.ComTempo(def.Id, c => provedor.ChamadaMinima(modelo, tokens, c), ct, TempoChamada);
            etapas.Add(new(EtapasFalha.ChamadaModelo, SituacoesEtapa.Ok, Ms(t0),
                cortada ? "O modelo respondeu (cortado no limite, mas funciona)" : $"O modelo respondeu em {Ms(t0)} ms", null));
        }
        catch (FalhaProvedorException f)
        {
            f.Etapa ??= EtapasFalha.ChamadaModelo;
            Logar(def.Id, modelo, f);
            var dto = CatalogoFalhas.Dto(f, def.Nome, modelo, agora, chaveColada is null ? origem : null, sugeridos);
            etapas.Add(new(EtapasFalha.ChamadaModelo, SituacoesEtapa.Falhou, Ms(t0), "O modelo não respondeu", dto));
            return Parar(dto);
        }

        // 5. ferramentas (só com a chave em uso: o teste passa pela cadeia de verdade)
        if (!ferramentas)
            etapas.Add(new(EtapasFalha.Ferramentas, SituacoesEtapa.Pulada, null, "Uso de ferramentas (peça a verificação completa)", null));
        else if (chaveColada is not null || checarFerramentas is null)
            etapas.Add(new(EtapasFalha.Ferramentas, SituacoesEtapa.Pulada, null, "Ferramentas só depois de salvar a chave", null));
        else
        {
            t0 = Stopwatch.GetTimestamp();
            var cfgTeste = ComItem(cfg, def.Id, modelo, out var itemId);
            var r = await checarFerramentas(cfgTeste, itemId, ct);
            if (!r.Ok)
            {
                var falha = r.Falha ?? r.Checagem?.FirstOrDefault(c => !c.Ok)?.Falha
                    ?? CatalogoFalhas.Dto(CatalogoFalhas.Nova(def.Id, CodigosFalha.FerramentaErrada, etapa: EtapasFalha.Ferramentas), def.Nome, modelo, agora, null, sugeridos)
                        with { Detalhe = r.Checagem?.FirstOrDefault(c => !c.Ok)?.Erro ?? r.Erro };
                falha = falha with { Etapa = EtapasFalha.Ferramentas, ModelosSugeridos = falha.ModelosSugeridos ?? (sugeridos.Count > 0 ? sugeridos : null) };
                var falhas = r.Checagem?.Count(c => !c.Ok) ?? 0;
                etapas.Add(new(EtapasFalha.Ferramentas, SituacoesEtapa.Falhou, Ms(t0), falhas > 0 ? $"{falhas} de 4 ferramentas falharam" : "As ferramentas falharam", falha));
                return Parar(falha);
            }
            etapas.Add(new(EtapasFalha.Ferramentas, SituacoesEtapa.Ok, Ms(t0), "As 4 ferramentas funcionaram", null));
        }

        bool? religado = null;
        if (chaveColada is null) religado = await ReligarSePreciso(cfg, def.Id, modelo, ct);
        return new DiagnosticoDto(def.Id, modelo, origemDto, true, etapas, null, religado, publica);
    }

    /// <summary>O §12.5 em cada item ligado com modelo, no máximo 4 ao mesmo tempo e 25 s cada.</summary>
    public async Task<VerificacaoDto> VerificarTudo(ConfigIA cfg, CancellationToken ct)
    {
        using var vez = new SemaphoreSlim(MaxParalelo);
        var itens = cfg.Cadeia.Where(i => i.Ligado && !string.IsNullOrWhiteSpace(i.Modelo) && cfg.ProvedorDe(i.Provedor) is not null).ToList();
        var resultados = await Task.WhenAll(itens.Select(async item =>
        {
            await vez.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TempoItem);
                try
                {
                    var d = await Diagnosticar(cfg, item.Provedor, item.Modelo, null, false, cts.Token);
                    return new DiagnosticoItemDto(item.Id, item.Provedor, d.Modelo, d.Ok, d.Falha, d.Etapas, d.Religado);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var def = cfg.ProvedorDe(item.Provedor)!;
                    var f = CatalogoFalhas.Dto(CatalogoFalhas.Nova(def.Id, CodigosFalha.TempoEsgotado, etapa: EtapasFalha.ChamadaModelo), def.Nome, item.Modelo, relogio.GetUtcNow());
                    return new DiagnosticoItemDto(item.Id, item.Provedor, item.Modelo, false, f, [], null);
                }
            }
            finally { vez.Release(); }
        }));
        return new VerificacaoDto(resultados);
    }

    private static readonly (string Etapa, string Texto)[] Ordem =
    [
        (EtapasFalha.FormatoChave, "Formato da chave"),
        (EtapasFalha.ListaModelos, "Lista de modelos"),
        (EtapasFalha.ModeloNaLista, "Modelo existe"),
        (EtapasFalha.ChamadaModelo, "Chamada ao modelo"),
        (EtapasFalha.Ferramentas, "Uso de ferramentas"),
    ];

    private static readonly HashSet<string> Conhecidos = [Provedores.Anthropic, Provedores.OpenAI, Provedores.Groq, Provedores.Gemini, Provedores.OpenRouter];
    private static bool Conhecido(string id) => Conhecidos.Contains(id);

    private static long Ms(long inicio) => (long)Stopwatch.GetElapsedTime(inicio).TotalMilliseconds;

    private static DefinicaoProvedor Definicao(ConfigIA cfg, string id) =>
        cfg.ProvedorDe(id) ?? throw new AssistenteException(404, "PROVEDOR_NAO_ENCONTRADO", "Esse provedor não está na configuração salva.");

    private (string? Valor, string Origem) Chave(DefinicaoProvedor def, string? colada) =>
        colada is not null ? (Limpar(colada), "colada") : criador.ChaveEmUso(def);

    /// <summary>A configuração com um item desse provedor + modelo para o teste de ferramentas (usa o da cadeia, se houver).</summary>
    private static ConfigIA ComItem(ConfigIA cfg, string provedor, string modelo, out string itemId)
    {
        var existente = cfg.Cadeia.FirstOrDefault(i => i.Provedor == provedor && i.Modelo == modelo);
        if (existente is not null) { itemId = existente.Id; return cfg; }
        itemId = "diagnostico";
        return cfg with { Cadeia = [.. cfg.Cadeia, new ItemCadeia(itemId, provedor, modelo)] };
    }

    private async Task<bool?> ReligarSePreciso(ConfigIA cfg, string provedor, string modelo, CancellationToken ct)
    {
        try
        {
            var foto = await estado.Foto(AssistenteService.InicioDoDia(relogio.GetUtcNow()), ct);
            var desligados = cfg.Cadeia.Where(i => i.Provedor == provedor && i.Modelo == modelo && RegrasCadeia.EstadoValido(i, foto) is { } e && (e.Desligado || e.EsgotadoAte > relogio.GetUtcNow())).ToList();
            foreach (var i in desligados) await estado.Religar(i.Id, ct);
            if (desligados.Count > 0) logger.LogInformation("Assistente: {Provedor}/{Modelo} passou no diagnóstico; {N} item(ns) religado(s)", provedor, modelo, desligados.Count);
            return desligados.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Assistente: não religou depois do diagnóstico ({Erro})", ex.GetType().Name);
            return null;
        }
    }

    private void Logar(string provedor, string? modelo, FalhaProvedorException f) =>
        logger.LogInformation("Assistente: diagnóstico de {Provedor}/{Modelo} falhou em {Etapa}: {Codigo} {Status} {CodigoProvedor} {Mensagem}",
            provedor, modelo, f.Etapa, f.Codigo, f.Status, f.CodigoProvedor, f.MensagemProvedor);
}
