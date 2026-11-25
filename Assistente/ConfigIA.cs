using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace APIBack.Assistente;

/// <summary>Ids dos provedores pré-cadastrados (CONTRATO-API §10.1).</summary>
public static class Provedores
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
    public const string Groq = "groq";
    public const string Gemini = "gemini";
    public const string OpenRouter = "openrouter";
}

public static class TiposProvedor
{
    public const string OpenAICompativel = "openai";
    public const string Anthropic = "anthropic";
}

public static class Esforcos
{
    public const string Nenhum = "nenhum";
    public static readonly string[] Todos = [Nenhum, "low", "medium", "high"];
}

public static class ModosCadeia
{
    public const string Ordem = "ordem";
    public const string Revezar = "revezar";
    public static readonly string[] Todos = [Ordem, Revezar];
}

public static class PoliticasPagos
{
    public const string PagoSoNoFim = "pagoSoNoFim";
    public const string NuncaPago = "nuncaPago";
    public static readonly string[] Todas = [PagoSoNoFim, NuncaPago];
}

/// <summary>
/// Um provedor de IA: onde fica (URL base), qual variável de ambiente tem a chave (a chave em si nunca vem para cá) e o que ele
/// aceita (CONTRATO-API §10.1). <see cref="AceitaStrict"/>: <c>strict</c> nas ferramentas. <see cref="AceitaEsforco"/>:
/// <c>reasoning_effort</c>. <see cref="EsquemaCompleto"/>: <c>additionalProperties</c> e tipos anuláveis no esquema.
/// </summary>
public sealed record DefinicaoProvedor(
    string Id, string Nome, string Tipo, string? UrlBase, string VariavelChave, bool Gratis, bool UsaDadosParaTreinar,
    bool AceitaStrict = false, bool AceitaEsforco = false, bool EsquemaCompleto = false, IReadOnlyDictionary<string, string>? Cabecalhos = null);

/// <summary>Um elo da cadeia: provedor + modelo, com limite próprio de pedidos por dia (opcional) e esforço próprio (opcional).</summary>
public sealed record ItemCadeia(string Id, string Provedor, string Modelo, int? LimiteDia = null, bool Ligado = true, string? Esforco = null);

/// <summary>US$ por milhão de tokens de um modelo.</summary>
public sealed record PrecoModelo(string Modelo, decimal Entrada, decimal Saida, decimal CacheLido);

/// <summary>Configuração da IA do assistente, escolhida na Gestão da plataforma (CONTRATO-API §7 e §10). Chave nunca entra aqui.</summary>
public sealed record ConfigIA
{
    public bool Ligado { get; init; } = true;
    public string Esforco { get; init; } = "low";
    public int MaxTokens { get; init; } = 4096;
    public int LimiteDiario { get; init; } = 100;
    public string Modo { get; init; } = ModosCadeia.Ordem;
    public string Pagos { get; init; } = PoliticasPagos.PagoSoNoFim;

    [JsonPropertyName("provedores")]
    public IReadOnlyList<DefinicaoProvedor> Definicoes { get; init; } = ProvedoresPadrao;
    public IReadOnlyList<ItemCadeia> Cadeia { get; init; } = CadeiaPadrao;
    /// <summary>US$ por milhão de tokens. Modelo fora da lista = sem preço (tokens contam, custo não). Provedor grátis = custo 0.</summary>
    public IReadOnlyList<PrecoModelo> Precos { get; init; } = PrecosPadrao;

    // Formato antigo (§7.1: principal + reserva). Só entrada: Normalizada() converte em cadeia e zera estes campos.
    public string? Provedor { get; init; }
    public string? Modelo { get; init; }
    public string? ReservaProvedor { get; init; }
    public string? ReservaModelo { get; init; }

    /// <summary>Provedores pré-cadastrados. Flags conferidas na doc oficial de cada um em 18/11/2026 (fontes no CONTRATO-API §10.1).</summary>
    public static readonly IReadOnlyList<DefinicaoProvedor> ProvedoresPadrao =
    [
        new(Provedores.Groq, "Groq", TiposProvedor.OpenAICompativel, "https://api.groq.com/openai/v1", "GROQ_API_KEY", true, false,
            AceitaStrict: false, AceitaEsforco: false, EsquemaCompleto: false),
        new(Provedores.Gemini, "Google Gemini", TiposProvedor.OpenAICompativel, "https://generativelanguage.googleapis.com/v1beta/openai/", "GEMINI_API_KEY", true, true,
            AceitaStrict: false, AceitaEsforco: true, EsquemaCompleto: false),
        new(Provedores.OpenRouter, "OpenRouter (modelos :free)", TiposProvedor.OpenAICompativel, "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", true, false,
            AceitaStrict: false, AceitaEsforco: false, EsquemaCompleto: false, Cabecalhos: new Dictionary<string, string> { ["X-OpenRouter-Title"] = "Zippy" }),
        new(Provedores.OpenAI, "OpenAI", TiposProvedor.OpenAICompativel, "https://api.openai.com/v1", "OPENAI_API_KEY", false, false,
            AceitaStrict: true, AceitaEsforco: true, EsquemaCompleto: true),
        new(Provedores.Anthropic, "Anthropic", TiposProvedor.Anthropic, null, "ANTHROPIC_API_KEY", false, false,
            AceitaStrict: true, AceitaEsforco: true, EsquemaCompleto: true),
    ];

    /// <summary>
    /// Sem configuração salva: grátis primeiro (Groq → Gemini → OpenRouter), OpenAI pago no fim. Modelos conferidos na doc oficial em
    /// 18/11/2026; o OpenRouter vem desligado e sem modelo porque nenhum <c>:free</c> com ferramentas pôde ser confirmado.
    /// Anthropic fica cadastrada e fora da cadeia.
    /// </summary>
    public static readonly IReadOnlyList<ItemCadeia> CadeiaPadrao =
    [
        new(Provedores.Groq, Provedores.Groq, "llama-3.3-70b-versatile"),
        // 25/11/2026: o gemini-3.8-flash deu 503 repetido na chave grátis; o 2.5 Flash respondeu.
        new(Provedores.Gemini, Provedores.Gemini, "gemini-2.5-flash", Esforco: "low"),
        new(Provedores.OpenRouter, Provedores.OpenRouter, "", LimiteDia: 45, Ligado: false),
        new(Provedores.OpenAI, Provedores.OpenAI, "gpt-5.4-mini", Esforco: "low"),
    ];

    public static readonly IReadOnlyList<PrecoModelo> PrecosPadrao =
    [
        new("claude-opus-5-5", 4m, 20m, 0.20m),
        new("claude-sonnet-5-5", 2m, 10m, 0.10m),
        new("claude-haiku-5-5", 0.10m, 0.50m, 0.01m),
        // developers.openai.com/api/docs/pricing, 18/11/2026: US$ 0,75 entrada / 0,075 cache / 4,50 saída por milhão.
        new("gpt-5.4-mini", 0.75m, 4.50m, 0.075m),
    ];

    private static readonly string[] CabecalhosProibidos = ["authorization", "api-key", "x-api-key", "host", "cookie", "proxy-authorization"];
    private static readonly Regex NomeCabecalho = new("^[A-Za-z0-9-]{1,60}$", RegexOptions.Compiled);

    /// <summary>Problema de um cabeçalho extra, ou nulo. Não deixa trocar autenticação nem corpo da chamada. Público para teste.</summary>
    public static string? ProblemaCabecalho(string nome, string? valor)
    {
        if (!NomeCabecalho.IsMatch(nome ?? "")) return "Nome de cabeçalho: letras, números e hífen.";
        var n = nome!.ToLowerInvariant();
        if (CabecalhosProibidos.Contains(n) || n.StartsWith("content-", StringComparison.Ordinal)) return $"O cabeçalho {nome} não pode ser trocado.";
        if (valor is null || valor.Length > 200 || valor.Any(char.IsControl)) return $"Valor do cabeçalho {nome}: até 200 caracteres, sem quebra de linha.";
        return null;
    }

    public static readonly ConfigIA Padrao = new();

    /// <summary>Variáveis que o servidor aceita ler como chave: as dos provedores pré-cadastrados ou ASSISTENTE_*_API_KEY.</summary>
    public static readonly string[] VariaveisConhecidas = ["OPENAI_API_KEY", "GROQ_API_KEY", "GEMINI_API_KEY", "OPENROUTER_API_KEY", "ANTHROPIC_API_KEY"];
    private static readonly Regex VariavelPropria = new("^ASSISTENTE_[A-Z0-9_]+_API_KEY$", RegexOptions.Compiled);
    private static readonly Regex IdValido = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static bool VariavelPermitida(string? nome) => nome is not null && (VariaveisConhecidas.Contains(nome) || VariavelPropria.IsMatch(nome));

    public PrecoModelo? PrecoDe(string modelo) => Precos.FirstOrDefault(p => p.Modelo == modelo);
    public DefinicaoProvedor? ProvedorDe(string id) => Definicoes.FirstOrDefault(p => p.Id == id);
    public ItemCadeia? ItemDe(string id) => Cadeia.FirstOrDefault(i => i.Id == id);

    /// <summary>Problema da configuração em uma frase para a tela, ou nulo. Chame em <see cref="Normalizada"/>. Público para teste.</summary>
    public string? Problema()
    {
        if (!Esforcos.Todos.Contains(Esforco)) return "Esforço deve ser nenhum, low, medium ou high.";
        if (MaxTokens is < 256 or > 32000) return "Máximo de tokens entre 256 e 32000.";
        if (LimiteDiario is < 1 or > 10000) return "Limite diário entre 1 e 10000 mensagens.";
        if (!ModosCadeia.Todos.Contains(Modo)) return "Modo deve ser ordem ou revezar.";
        if (!PoliticasPagos.Todas.Contains(Pagos)) return "Pagos deve ser pagoSoNoFim ou nuncaPago.";

        if (Definicoes is null || Definicoes.Count == 0) return "Cadastre ao menos um provedor.";
        foreach (var p in Definicoes)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || !IdValido.IsMatch(p.Id) || p.Id.Length > 30) return "Id de provedor: minúsculas, números e hífen (até 30).";
            if (string.IsNullOrWhiteSpace(p.Nome) || p.Nome.Length > 60) return $"Dê um nome ao provedor {p.Id} (até 60 caracteres).";
            if (p.Tipo is not (TiposProvedor.OpenAICompativel or TiposProvedor.Anthropic)) return $"Tipo do provedor {p.Id} deve ser openai ou anthropic.";
            if (p.Tipo == TiposProvedor.OpenAICompativel && (!Uri.TryCreate(p.UrlBase, UriKind.Absolute, out var u) || u.Scheme != "https"))
                return $"URL base do provedor {p.Id} precisa começar com https://.";
            // Vazia = só a chave cadastrada na Gestão (banco). Preenchida = só as variáveis permitidas.
            if (!string.IsNullOrWhiteSpace(p.VariavelChave) && !VariavelPermitida(p.VariavelChave))
                return $"Variável da chave do provedor {p.Id}: deixe vazia (chave pela tela) ou use uma das conhecidas ou ASSISTENTE_..._API_KEY.";
            if (p.Cabecalhos is { Count: > 5 }) return $"No máximo 5 cabeçalhos extras no provedor {p.Id}.";
            foreach (var (nome, valor) in p.Cabecalhos ?? new Dictionary<string, string>())
                if (ProblemaCabecalho(nome, valor) is string pc) return $"{p.Id}: {pc}";
        }
        if (Definicoes.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } pr) return $"O provedor {pr.Key} aparece duas vezes.";

        if (Cadeia is null || Cadeia.Count is 0 or > 10) return "A cadeia precisa de 1 a 10 itens.";
        foreach (var i in Cadeia)
        {
            if (i is null || string.IsNullOrWhiteSpace(i.Id) || i.Id.Length > 20 || !IdValido.IsMatch(i.Id)) return "Id de item da cadeia: minúsculas, números e hífen (até 20).";
            if (ProvedorDe(i.Provedor) is null) return $"O item {i.Id} usa o provedor {i.Provedor}, que não está cadastrado.";
            if ((i.Modelo ?? "").Trim().Length > 100) return $"Modelo do item {i.Id}: até 100 caracteres.";
            if (i.Ligado && string.IsNullOrWhiteSpace(i.Modelo)) return $"Informe o modelo do item {i.Id} (ou desligue o item).";
            if (i.LimiteDia is < 1 or > 100000) return $"Limite do dia do item {i.Id} entre 1 e 100000.";
            if (i.Esforco is not null && !Esforcos.Todos.Contains(i.Esforco)) return $"Esforço do item {i.Id} deve ser nenhum, low, medium ou high.";
        }
        if (Cadeia.GroupBy(i => i.Id).FirstOrDefault(g => g.Count() > 1) is { } ir) return $"O item {ir.Key} aparece duas vezes na cadeia.";
        if (!Cadeia.Any(i => i.Ligado)) return "Deixe ao menos um item da cadeia ligado.";

        if (Precos is null) return "Envie a tabela de preços (pode ser vazia).";
        if (Precos.Any(p => p is null || string.IsNullOrWhiteSpace(p.Modelo) || p.Modelo.Trim().Length > 100))
            return "Cada preço precisa do nome do modelo (até 100 caracteres).";
        if (Precos.Any(p => p.Entrada < 0 || p.Saida < 0 || p.CacheLido < 0)) return "Preço não pode ser negativo.";
        if (Precos.GroupBy(p => p.Modelo.Trim()).FirstOrDefault(g => g.Count() > 1) is { } rep) return $"O modelo {rep.Key} aparece duas vezes nos preços.";
        return null;
    }

    /// <summary>
    /// Limpa espaços e converte o formato antigo: <c>provedor</c>/<c>modelo</c> (+ reserva) viram uma cadeia de 1 ou 2 itens.
    /// O formato antigo, quando vem, ganha da cadeia.
    /// </summary>
    public ConfigIA Normalizada()
    {
        var cadeia = Cadeia;
        if (!string.IsNullOrWhiteSpace(Provedor) && !string.IsNullOrWhiteSpace(Modelo))
        {
            var lista = new List<ItemCadeia> { new("principal", Provedor.Trim(), Modelo.Trim()) };
            if (!string.IsNullOrWhiteSpace(ReservaProvedor) && !string.IsNullOrWhiteSpace(ReservaModelo))
                lista.Add(new ItemCadeia("reserva", ReservaProvedor.Trim(), ReservaModelo.Trim()));
            cadeia = lista;
        }
        return this with
        {
            Cadeia = cadeia?.Where(i => i is not null).Select(i => i with { Id = (i.Id ?? "").Trim(), Provedor = (i.Provedor ?? "").Trim(), Modelo = CatalogoModelos.Normalizar((i.Modelo ?? "").Trim()) }).ToList() ?? cadeia!,
            Definicoes = Definicoes?.Where(p => p is not null).Select(p => p with { Id = (p.Id ?? "").Trim(), Nome = (p.Nome ?? "").Trim(), UrlBase = p.UrlBase?.Trim(), VariavelChave = (p.VariavelChave ?? "").Trim() }).ToList() ?? Definicoes!,
            Precos = Precos?.Where(p => p is not null).Select(p => p with { Modelo = (p.Modelo ?? "").Trim() }).ToList() ?? Precos!,
            Provedor = null, Modelo = null, ReservaProvedor = null, ReservaModelo = null,
        };
    }
}

public sealed record ConfigIASalva(ConfigIA Config, bool Padrao, string? AtualizadoPor, DateTimeOffset? AtualizadoEm);
