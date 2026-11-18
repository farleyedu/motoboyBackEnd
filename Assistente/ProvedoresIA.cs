using System.Collections.Concurrent;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

namespace APIBack.Assistente;

/// <summary>Uma linha da conversa já guardada (só texto). Papel: <c>usuario</c> | <c>assistente</c>.</summary>
public sealed record LinhaConversa(string Papel, string Texto);

/// <summary>
/// Tudo que um provedor precisa para responder um turno. As ferramentas vêm no formato neutro de <see cref="FerramentasAssistente"/>.
/// <see cref="NaoSuporta"/>: o provedor avisa quando descobriu que não aceita um parâmetro (a cadeia grava).
/// </summary>
public sealed record PedidoIA(
    string Modelo, string Esforco, int MaxTokens, string Sistema, IReadOnlyList<JsonObject> Ferramentas,
    IReadOnlyList<LinhaConversa> Historico, string Contexto, string Texto, Action<string>? NaoSuporta = null);

public enum DesfechoIA { Normal, Recusa, Cortada, SemFim }

public sealed record RespostaIA(string Texto, DesfechoIA Desfecho, long Entrada, long Saida, long CacheLidos);

public sealed record ResultadoFerramenta(string Conteudo, bool Erro);

public delegate Task<ResultadoFerramenta> ExecutarFerramenta(string nome, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);

/// <summary>Por que um provedor falhou: decide se pula para o próximo item, se marca "esgotado até" ou se desliga o item.</summary>
public enum TipoFalha
{
    /// <summary>429 ou cota esgotada: marca esgotado (retry-after, meia-noite se for cota do dia, ou 5 min).</summary>
    Limite,
    /// <summary>5xx, rede, tempo esgotado: marca esgotado por 5 min (ou retry-after).</summary>
    Servidor,
    /// <summary>Sem chave no servidor: pula, sem marcar.</summary>
    SemChave,
    /// <summary>Modelo inexistente, chave recusada: desliga o item até a Gestão religar.</summary>
    Configuracao,
    /// <summary>O provedor recusou o pedido (400): pula, sem marcar.</summary>
    Pedido,
    /// <summary>A resposta não serve (vazia, cortada, recusa de conteúdo, ferramenta errada duas vezes, voltas demais): pula, sem marcar.</summary>
    Resposta,
}

public sealed class FalhaProvedorException(string provedor, string mensagem, TipoFalha tipo, Exception? inner = null,
    TimeSpan? retryAfter = null, bool cotaDiaria = false, int? status = null) : Exception(mensagem, inner)
{
    public string Provedor { get; } = provedor;
    public TipoFalha Tipo { get; } = tipo;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool CotaDiaria { get; } = cotaDiaria;
    /// <summary>Código HTTP, quando houve (vai para o log; a mensagem não).</summary>
    public int? Status { get; } = status;
    public bool Transitoria => Tipo is TipoFalha.Limite or TipoFalha.Servidor or TipoFalha.SemChave;
}

public static class MensagensFalha
{
    public const string ChaveRecusada = "Chave recusada";
    public const string ModeloNaoExiste = "Modelo não existe";
    public const string RespostaVazia = "Resposta vazia";
    public const string RespostaCortada = "Resposta cortada (limite de tokens)";
    public const string Recusa = "Recusou o conteúdo";
    public const string FerramentaErrada = "Errou a chamada de ferramenta duas vezes";
    public const string VoltasDemais = "Ferramentas demais na mesma pergunta";
    public const string TempoEsgotado = "Passou do tempo limite";
    public const int MaxTamanho = 300;

    /// <summary>Corta a mensagem do provedor (pode trazer corpo da resposta) para guardar e mostrar.</summary>
    public static string Curta(string? m) => string.IsNullOrWhiteSpace(m) ? "" : m.Length <= MaxTamanho ? m : m[..(MaxTamanho - 1)] + "…";
}

/// <summary>
/// Confere cada chamada de ferramenta antes de executar: nome existe, argumentos são JSON e passam na leitura do chat. Na primeira
/// chamada ruim devolve o erro para a própria IA corrigir; na segunda, desiste (o item é pulado). Público para teste.
/// </summary>
public sealed class ControleFerramentas(string provedor, IEnumerable<JsonObject> ferramentas)
{
    private readonly HashSet<string> _nomes = ferramentas.Select(f => f["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    private int _erros;

    /// <summary>Alguma ferramenta foi executada neste turno (resposta sem texto mas com ferramenta não é "vazia").</summary>
    public bool ExecutouAlguma { get; private set; }

    /// <summary>Nulo = pode executar. Texto = devolva como erro à IA. Lança na segunda chamada ruim.</summary>
    public string? Conferir(string nome, IReadOnlyDictionary<string, JsonElement>? args)
    {
        var erro = !_nomes.Contains(nome) ? $"A ferramenta {nome} não existe. Use só: {string.Join(", ", _nomes)}."
            : args is null ? "Os argumentos não são um JSON válido. Mande um objeto JSON."
            : AssistenteService.ValidarArgumentos(nome, args) is string e ? $"Argumentos inválidos: {e}"
            : null;
        if (erro is null)
        {
            ExecutouAlguma = true;
            return null;
        }
        if (++_erros > 1) throw new FalhaProvedorException(provedor, MensagensFalha.FerramentaErrada, TipoFalha.Resposta);
        return erro;
    }

    /// <summary>Final do turno: texto vazio sem nenhuma ferramenta executada = resposta vazia.</summary>
    public void ExigirConteudo(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto) && !ExecutouAlguma) throw new FalhaProvedorException(provedor, MensagensFalha.RespostaVazia, TipoFalha.Resposta);
    }
}

/// <summary>Um provedor de IA configurado (Anthropic nativo ou compatível com OpenAI). Faz o laço de ferramentas do turno.</summary>
public interface IProvedorIA
{
    string Nome { get; }
    bool TemChave { get; }
    Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct);
    Task<IReadOnlyList<string>> Modelos(CancellationToken ct);
}

/// <summary>Cria o cliente de cada provedor da configuração e lê a chave da variável de ambiente indicada (só as permitidas).</summary>
public interface IFabricaProvedores
{
    IProvedorIA Criar(DefinicaoProvedor definicao);
    bool TemChave(DefinicaoProvedor definicao);
}

public sealed class FabricaProvedores(IConfiguration config, ILoggerFactory logs) : IFabricaProvedores
{
    public IProvedorIA Criar(DefinicaoProvedor d) => d.Tipo == TiposProvedor.Anthropic
        ? new ProvedorAnthropic(d.Id, Chave(d), logs.CreateLogger<ProvedorAnthropic>())
        : new ProvedorOpenAI(d, Chave(d), logs.CreateLogger<ProvedorOpenAI>());

    public bool TemChave(DefinicaoProvedor d) => Chave(d) is not null;

    /// <summary>Lê a variável do provedor. Variável fora da lista permitida não é lida (a Gestão não pode apontar para outro segredo).</summary>
    private string? Chave(DefinicaoProvedor d)
    {
        if (!ConfigIA.VariavelPermitida(d.VariavelChave)) return null;
        var valor = config[d.VariavelChave];
        if (string.IsNullOrWhiteSpace(valor) && d.VariavelChave == "ANTHROPIC_API_KEY") valor = AssistenteService.ChaveAnthropic(config);
        if (string.IsNullOrWhiteSpace(valor) && d.VariavelChave == "OPENAI_API_KEY") valor = AssistenteService.ChaveOpenAI(config);
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }
}

/// <summary>Lista de modelos de cada provedor, guardada por 1 hora. Falhou ou sem chave: lista vazia.</summary>
public sealed class CacheModelosIA(IFabricaProvedores fabrica, ILogger<CacheModelosIA> logger, TimeProvider relogio)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Em, IReadOnlyList<string> Lista)> _cache = new();

    public async Task<IReadOnlyList<string>> Modelos(DefinicaoProvedor d, CancellationToken ct)
    {
        var chave = $"{d.Id}|{d.UrlBase}|{d.VariavelChave}";
        if (_cache.TryGetValue(chave, out var c) && relogio.GetUtcNow() - c.Em < TimeSpan.FromHours(1)) return c.Lista;
        if (!fabrica.TemChave(d)) return [];
        try
        {
            var lista = await fabrica.Criar(d).Modelos(ct);
            _cache[chave] = (relogio.GetUtcNow(), lista);
            return lista;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Assistente: não foi possível listar modelos de {Provedor} ({Erro})", d.Id, ex.GetType().Name);
            return [];
        }
    }
}

/// <summary>Limites de cada chamada a uma IA. Sem repetição automática do SDK: quem repete é a cadeia, no próximo item.</summary>
public static class LimitesChamada
{
    public static readonly TimeSpan Tempo = TimeSpan.FromSeconds(30);
    public const int MaxRodadas = 6;

    /// <summary>Roda a chamada com 30 s. Estourou (sem ser cancelamento de quem pediu) = falha de servidor "tempo esgotado".</summary>
    public static async Task<T> ComTempo<T>(string provedor, Func<CancellationToken, Task<T>> chamada, CancellationToken ct, TimeSpan? tempo = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tempo ?? Tempo);
        try
        {
            return await chamada(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FalhaProvedorException(provedor, MensagensFalha.TempoEsgotado, TipoFalha.Servidor);
        }
    }
}

// ============================================================================================ Anthropic

/// <summary>Claude pelo SDK oficial <c>Anthropic</c> (endpoint beta, por causa do fallback no servidor).</summary>
public sealed class ProvedorAnthropic(string id, string? chave, ILogger logger) : IProvedorIA
{
    private const string BetaFallback = "server-side-fallback-2026-07-01";
    /// <summary>Modelos que aceitam <c>fallbacks: "default"</c> no servidor. Outros (Haiku, mais antigos) vão sem.</summary>
    private static readonly string[] ComFallbackNoServidor = ["claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5", "claude-fable-5-1", "claude-fable-5-2"];

    public string Nome => id;
    public bool TemChave => chave is not null;

    /// <summary>Ferramenta neutra → ferramenta da Anthropic (o JSON neutro já é o formato da API). Público para teste.</summary>
    public static BetaTool Traduzir(JsonObject ferramenta) =>
        BetaTool.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ferramenta.ToJsonString())!);

    public static bool UsaFallbackNoServidor(string modelo) => ComFallbackNoServidor.Contains(modelo);

    private AnthropicClient Cliente() => new()
    {
        ApiKey = chave ?? throw new FalhaProvedorException(id, "Chave não configurada", TipoFalha.SemChave),
        MaxRetries = 0,
        Timeout = LimitesChamada.Tempo,
    };

    public async Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct)
    {
        var client = Cliente();
        var controle = new ControleFerramentas(id, pedido.Ferramentas);
        var mensagens = pedido.Historico.Select(l => l.Papel == "usuario"
            ? (BetaMessageParam)new BetaUserMessageParam { Content = l.Texto }
            : new BetaAssistantMessageParam { Content = l.Texto }).ToList();
        mensagens.Add(new BetaUserMessageParam
        {
            Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = pedido.Contexto }, new BetaTextBlockParam { Text = pedido.Texto } },
        });
        var ferramentas = pedido.Ferramentas.Select(f => (BetaToolUnion)Traduzir(f)).ToList();
        var fallback = UsaFallbackNoServidor(pedido.Modelo);

        long entrada = 0, saida = 0, cache = 0;
        var textos = new List<string>();
        for (var rodada = 0; rodada < LimitesChamada.MaxRodadas; rodada++)
        {
            var parametros = new MessageCreateParams
            {
                Model = pedido.Modelo,
                MaxTokens = pedido.MaxTokens,
                System = new List<BetaTextBlockParam> { new() { Text = pedido.Sistema, CacheControl = new BetaCacheControlEphemeral() } },
                Tools = ferramentas,
                ToolChoice = new BetaToolChoiceAuto(),
                Messages = mensagens,
            };
            if (pedido.Esforco != Esforcos.Nenhum) parametros = parametros with { OutputConfig = new BetaOutputConfig { Effort = EsforcoAnthropic(pedido.Esforco) } };
            if (fallback) parametros = parametros with { Betas = [BetaFallback], Fallbacks = new Default() };

            BetaMessage r;
            try
            {
                r = await LimitesChamada.ComTempo(id, c => client.Beta.Messages.Create(parametros, cancellationToken: c), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw Classificar(ex, id);
            }

            entrada += r.Usage.InputTokens + (r.Usage.CacheCreationInputTokens ?? 0) + (r.Usage.CacheReadInputTokens ?? 0);
            cache += r.Usage.CacheReadInputTokens ?? 0;
            saida += r.Usage.OutputTokens;

            if (r.StopReason == BetaStopReason.Refusal)
            {
                logger.LogInformation("Assistente/{Provedor}: recusa ({Categoria})", id, r.StopDetails?.Category);
                throw new FalhaProvedorException(id, MensagensFalha.Recusa, TipoFalha.Resposta);
            }
            if (r.StopReason == BetaStopReason.MaxTokens) throw new FalhaProvedorException(id, MensagensFalha.RespostaCortada, TipoFalha.Resposta);
            textos.AddRange(r.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text.Trim()).Where(t => t.Length > 0));

            var usos = r.Content.Select(b => b.Value).OfType<BetaToolUseBlock>().ToList();
            if (r.StopReason != BetaStopReason.ToolUse || usos.Count == 0)
            {
                var texto = string.Join("\n\n", textos);
                controle.ExigirConteudo(texto);
                return new RespostaIA(texto, DesfechoIA.Normal, entrada, saida, cache);
            }

            mensagens.Add(new BetaAssistantMessageParam { Content = Eco(r.Content) });
            var resultados = new List<BetaContentBlockParam>();
            foreach (var u in usos)
            {
                var problema = controle.Conferir(u.Name, u.Input);
                var res = problema is null ? await executar(u.Name, u.Input, ct) : new ResultadoFerramenta(problema, true);
                resultados.Add(new BetaToolResultBlockParam { ToolUseID = u.ID, Content = res.Conteudo, IsError = res.Erro });
            }
            mensagens.Add(new BetaUserMessageParam { Content = resultados });
        }
        throw new FalhaProvedorException(id, MensagensFalha.VoltasDemais, TipoFalha.Resposta);
    }

    private static Effort EsforcoAnthropic(string e) => e switch { "medium" => Effort.Medium, "high" => Effort.High, _ => Effort.Low };

    public static FalhaProvedorException Classificar(Exception ex, string id = Provedores.Anthropic) => ex switch
    {
        FalhaProvedorException f => f,
        Anthropic5xxException => new(id, "Erro no servidor do provedor", TipoFalha.Servidor, ex),
        AnthropicRateLimitException => new(id, "Limite de uso atingido", TipoFalha.Limite, ex, status: 429),
        AnthropicIOException or HttpRequestException or TaskCanceledException => new(id, "Sem conexão", TipoFalha.Servidor, ex),
        AnthropicNotFoundException => new(id, MensagensFalha.ModeloNaoExiste, TipoFalha.Configuracao, ex, status: 404),
        AnthropicUnauthorizedException or AnthropicForbiddenException => new(id, MensagensFalha.ChaveRecusada, TipoFalha.Configuracao, ex, status: 401),
        AnthropicBadRequestException => new(id, $"Pedido recusado (400). {MensagensFalha.Curta(ex.Message)}", TipoFalha.Pedido, ex, status: 400),
        _ => new(id, $"Erro: {MensagensFalha.Curta(ex.Message)}", TipoFalha.Pedido, ex),
    };

    /// <summary>Resposta do modelo de volta como assistente no mesmo turno (pensamento intacto; regra do bloco de fallback).</summary>
    private static List<BetaContentBlockParam> Eco(IReadOnlyList<BetaContentBlock> conteudo)
    {
        var ultimoFallback = -1;
        for (var i = 0; i < conteudo.Count; i++) if (conteudo[i].TryPickFallback(out _)) ultimoFallback = i;
        var eco = new List<BetaContentBlockParam>();
        for (var i = 0; i < conteudo.Count; i++)
        {
            var b = conteudo[i];
            if (b.TryPickText(out var t)) eco.Add(new BetaTextBlockParam { Text = t.Text });
            else if (b.TryPickFallback(out var f))
                eco.Add(BetaFallbackBlockParam.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(f))!));
            else if (i < ultimoFallback) continue;
            else if (b.TryPickThinking(out var th)) eco.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (b.TryPickRedactedThinking(out var rt)) eco.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            else if (b.TryPickToolUse(out var tu)) eco.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
        }
        return eco;
    }

    public async Task<IReadOnlyList<string>> Modelos(CancellationToken ct)
    {
        var pagina = await Cliente().Models.List(cancellationToken: ct);
        return pagina.Items.Select(m => m.ID).Where(m => m.StartsWith("claude", StringComparison.Ordinal)).Distinct().ToList();
    }
}

// ============================================================================================ compatível com OpenAI

/// <summary>Põe os cabeçalhos extras do provedor (ex.: OpenRouter) em toda chamada.</summary>
internal sealed class CabecalhosExtras(IReadOnlyDictionary<string, string> cabecalhos) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Aplicar(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Aplicar(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Aplicar(PipelineMessage message)
    {
        foreach (var (nome, valor) in cabecalhos) message.Request.Headers.Set(nome, valor);
    }
}

/// <summary>
/// Qualquer API compatível com a da OpenAI (OpenAI, Groq, Gemini, OpenRouter…) pelo SDK oficial <c>OpenAI</c>, Chat Completions
/// com ferramentas. URL base, chave, cabeçalhos e o que o provedor aceita vêm da <see cref="DefinicaoProvedor"/>. Se o provedor
/// recusar <c>strict</c>, <c>reasoning_effort</c> ou o esquema, tenta uma vez sem e avisa pela <see cref="PedidoIA.NaoSuporta"/>.
/// </summary>
public sealed class ProvedorOpenAI(DefinicaoProvedor definicao, string? chave, ILogger logger) : IProvedorIA
{
    private static readonly string[] NaoChat = ["embedding", "whisper", "tts", "dall-e", "moderation", "image", "audio", "realtime", "transcribe", "search", "davinci", "babbage"];
    private static readonly string[] SinaisCotaDiaria = ["per day", "per-day", "daily", "rpd", "requests per day", "insufficient_quota", "free-models-per-day", "quota"];

    public const string ParamStrict = "strict";
    public const string ParamEsforco = "reasoning_effort";
    public const string ParamEsquema = "esquema";

    public string Nome => definicao.Id;
    public bool TemChave => chave is not null;

    /// <summary>Ferramenta neutra → função da OpenAI, ajustada ao que o provedor aceita. Público para teste.</summary>
    public static ChatTool Traduzir(JsonObject ferramenta, bool strict = true, bool esquemaCompleto = true)
    {
        var f = FerramentasAssistente.Adaptar(ferramenta, strict, esquemaCompleto);
        return ChatTool.CreateFunctionTool(
            f["name"]!.GetValue<string>(),
            f["description"]?.GetValue<string>(),
            BinaryData.FromString(f["input_schema"]!.ToJsonString()),
            strict ? true : null);
    }

    /// <summary>Argumentos da chamada (texto JSON) → dicionário igual ao da Anthropic. JSON inválido (ou não objeto) = nulo. Público para teste.</summary>
    public static IReadOnlyDictionary<string, JsonElement>? LerArgumentos(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Qual parâmetro o 400 recusou: <c>strict</c>, <c>reasoning_effort</c>, <c>esquema</c> ou nenhum conhecido. Público para teste.</summary>
    public static string? ParametroRecusado(string? mensagem)
    {
        if (string.IsNullOrWhiteSpace(mensagem)) return null;
        var m = mensagem.ToLowerInvariant();
        if (m.Contains("reasoning")) return ParamEsforco;
        if (m.Contains("strict")) return ParamStrict;
        if (m.Contains("additionalproperties") || m.Contains("schema") || m.Contains("anyof") || m.Contains("\"type\"") || m.Contains("invalid type"))
            return ParamEsquema;
        return null;
    }

    private OpenAIClientOptions Opcoes()
    {
        var o = new OpenAIClientOptions
        {
            Endpoint = new Uri(definicao.UrlBase!),
            NetworkTimeout = LimitesChamada.Tempo,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        if (definicao.Cabecalhos is { Count: > 0 } c) o.AddPolicy(new CabecalhosExtras(c), PipelinePosition.PerCall);
        return o;
    }

    public async Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct)
    {
        var client = new ChatClient(pedido.Modelo,
            new ApiKeyCredential(chave ?? throw new FalhaProvedorException(Nome, "Chave não configurada", TipoFalha.SemChave)), Opcoes());
        var controle = new ControleFerramentas(Nome, pedido.Ferramentas);
        var mensagens = new List<ChatMessage> { new SystemChatMessage(pedido.Sistema) };
        foreach (var l in pedido.Historico)
            mensagens.Add(l.Papel == "usuario" ? new UserChatMessage(l.Texto) : new AssistantChatMessage(l.Texto));
        mensagens.Add(new UserChatMessage(pedido.Contexto + "\n\n" + pedido.Texto));

        var (strict, esforco, completo) = (definicao.AceitaStrict, definicao.AceitaEsforco && pedido.Esforco != Esforcos.Nenhum, definicao.EsquemaCompleto);
        ChatCompletionOptions Montar()
        {
            var o = new ChatCompletionOptions { MaxOutputTokenCount = pedido.MaxTokens, ToolChoice = ChatToolChoice.CreateAutoChoice() };
            foreach (var f in pedido.Ferramentas) o.Tools.Add(Traduzir(f, strict, completo));
#pragma warning disable OPENAI001 // reasoning_effort ainda é "avaliação" no SDK; o campo da API é estável.
            if (esforco) o.ReasoningEffortLevel = new ChatReasoningEffortLevel(pedido.Esforco);
#pragma warning restore OPENAI001
            return o;
        }
        // Desliga o parâmetro recusado (uma vez cada) e avisa. Falso = já estava desligado: o 400 é outro problema.
        bool Desligar(string param)
        {
            var estava = param switch { ParamStrict => strict, ParamEsforco => esforco, ParamEsquema => completo, _ => false };
            if (!estava) return false;
            if (param == ParamStrict) strict = false;
            else if (param == ParamEsforco) esforco = false;
            else completo = false;
            logger.LogInformation("Assistente/{Provedor}: não aceita {Parametro}; tentando sem", Nome, param);
            pedido.NaoSuporta?.Invoke(param);
            return true;
        }

        var opcoes = Montar();
        long entrada = 0, saida = 0, cache = 0;
        var textos = new List<string>();
        for (var rodada = 0; rodada < LimitesChamada.MaxRodadas; rodada++)
        {
            ChatCompletion c;
            while (true)
            {
                try
                {
                    c = (await LimitesChamada.ComTempo(Nome, t => client.CompleteChatAsync(mensagens, opcoes, t), ct)).Value;
                    break;
                }
                catch (ClientResultException r) when (r.Status is 400 or 422 && ParametroRecusado(r.Message) is string p && Desligar(p))
                {
                    opcoes = Montar();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    throw Classificar(ex, Nome);
                }
            }

            if (c.Usage is { } u)
            {
                entrada += u.InputTokenCount;
                saida += u.OutputTokenCount;
                cache += u.InputTokenDetails?.CachedTokenCount ?? 0;
            }
            if (!string.IsNullOrEmpty(c.Refusal) || c.FinishReason == ChatFinishReason.ContentFilter)
            {
                logger.LogInformation("Assistente/{Provedor}: recusa ({Motivo})", Nome, c.FinishReason);
                throw new FalhaProvedorException(Nome, MensagensFalha.Recusa, TipoFalha.Resposta);
            }
            if (c.FinishReason == ChatFinishReason.Length) throw new FalhaProvedorException(Nome, MensagensFalha.RespostaCortada, TipoFalha.Resposta);
            textos.AddRange(c.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text.Trim()).Where(t => t.Length > 0));
            if (c.ToolCalls.Count == 0)
            {
                var texto = string.Join("\n\n", textos);
                controle.ExigirConteudo(texto);
                return new RespostaIA(texto, DesfechoIA.Normal, entrada, saida, cache);
            }

            mensagens.Add(new AssistantChatMessage(c));
            foreach (var chamada in c.ToolCalls)
            {
                var args = LerArgumentos(chamada.FunctionArguments.ToString());
                var problema = controle.Conferir(chamada.FunctionName, args);
                var res = problema is null ? await executar(chamada.FunctionName, args!, ct) : new ResultadoFerramenta(problema, true);
                // Chat Completions não tem marca de erro no resultado: vai no texto.
                mensagens.Add(new ToolChatMessage(chamada.Id, res.Erro ? "ERRO: " + res.Conteudo : res.Conteudo));
            }
        }
        throw new FalhaProvedorException(Nome, MensagensFalha.VoltasDemais, TipoFalha.Resposta);
    }

    /// <summary>Erro do SDK → tipo de falha, com retry-after e "cota do dia" quando o provedor diz. Público para teste.</summary>
    public static FalhaProvedorException Classificar(Exception ex, string id = Provedores.OpenAI)
    {
        if (ex is FalhaProvedorException f) return f;
        if (ex is ClientResultException r) return PorStatus(id, r.Status, r.Message, RetryAfter(r), ex);
        if (ex is HttpRequestException or TaskCanceledException) return new(id, "Sem conexão", TipoFalha.Servidor, ex);
        return new(id, $"Erro: {MensagensFalha.Curta(ex.Message)}", TipoFalha.Pedido, ex);
    }

    /// <summary>Código HTTP → tipo de falha e mensagem que vai para a Gestão. Público para teste.</summary>
    public static FalhaProvedorException PorStatus(string id, int status, string? mensagem, TimeSpan? retry = null, Exception? ex = null) => status switch
    {
        429 => new(id, "Limite de uso atingido", TipoFalha.Limite, ex, retry, CotaDiaria(mensagem), 429),
        >= 500 => new(id, "Erro no servidor do provedor", TipoFalha.Servidor, ex, retry, status: status),
        0 => new(id, "Sem conexão", TipoFalha.Servidor, ex),
        404 => new(id, MensagensFalha.ModeloNaoExiste, TipoFalha.Configuracao, ex, status: 404),
        401 or 403 => new(id, MensagensFalha.ChaveRecusada, TipoFalha.Configuracao, ex, status: status),
        402 => new(id, "Sem crédito na conta (402)", TipoFalha.Limite, ex, retry, true, 402),
        _ => new(id, $"Pedido recusado ({status}). {MensagensFalha.Curta(mensagem)}", TipoFalha.Pedido, ex, status: status),
    };

    /// <summary>Mensagem de 429 fala de limite diário/cota? Público para teste.</summary>
    public static bool CotaDiaria(string? mensagem) =>
        mensagem is not null && SinaisCotaDiaria.Any(s => mensagem.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan? RetryAfter(ClientResultException r)
    {
        try
        {
            if (r.GetRawResponse()?.Headers.TryGetValue("retry-after", out var v) == true) return LerRetryAfter(v, DateTimeOffset.UtcNow);
        }
        catch (Exception) { /* sem cabeçalho legível */ }
        return null;
    }

    /// <summary>Retry-After em segundos ou data HTTP. Público para teste.</summary>
    public static TimeSpan? LerRetryAfter(string? valor, DateTimeOffset agora)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        if (double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0) return TimeSpan.FromSeconds(s);
        if (DateTimeOffset.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var quando) && quando > agora) return quando - agora;
        return null;
    }

    public async Task<IReadOnlyList<string>> Modelos(CancellationToken ct)
    {
        var client = new OpenAIModelClient(new ApiKeyCredential(chave!), Opcoes());
        var lista = (await client.GetModelsAsync(ct)).Value;
        return FiltrarModelosChat(lista.Select(m => m.Id));
    }

    /// <summary>Só modelos de conversa (tira embedding, áudio, imagem…). Público para teste.</summary>
    public static IReadOnlyList<string> FiltrarModelosChat(IEnumerable<string> ids) => ids
        .Where(id => !NaoChat.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase)))
        .Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
}
