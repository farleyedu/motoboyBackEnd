using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIBack.Assistente;

// Diagnóstico de falhas das IAs (CONTRATO-API §12). Tudo aqui é sem rede nem banco: público para teste.

public static class CodigosFalha
{
    public const string SemChave = "sem_chave";
    public const string ChaveFormato = "chave_formato";
    public const string ChaveOutroProvedor = "chave_outro_provedor";
    public const string ChaveRecusada = "chave_recusada";
    public const string ChaveAmbienteRecusada = "chave_ambiente_recusada";
    public const string SemPermissao = "sem_permissao";
    public const string RegiaoBloqueada = "regiao_bloqueada";
    public const string SemCredito = "sem_credito";
    public const string LimiteMinuto = "limite_minuto";
    public const string CotaDia = "cota_dia";
    public const string ModeloNaoExiste = "modelo_nao_existe";
    public const string ModeloSemAcesso = "modelo_sem_acesso";
    public const string ModeloNaoConversa = "modelo_nao_conversa";
    public const string ModeloSemFerramentas = "modelo_sem_ferramentas";
    public const string ParametroNaoSuportado = "parametro_nao_suportado";
    public const string PedidoGrande = "pedido_grande";
    public const string PedidoRecusado = "pedido_recusado";
    public const string ModeloSobrecarregado = "modelo_sobrecarregado";
    public const string ErroInternoProvedor = "erro_interno_provedor";
    public const string TempoEsgotado = "tempo_esgotado";
    public const string SemConexao = "sem_conexao";
    public const string UrlErrada = "url_errada";
    public const string ConteudoBloqueado = "conteudo_bloqueado";
    public const string RespostaVazia = "resposta_vazia";
    public const string RespostaCortada = "resposta_cortada";
    public const string FerramentaErrada = "ferramenta_errada";
    public const string VoltasDemais = "voltas_demais";
    public const string CifraIndisponivel = "cifra_indisponivel";
    public const string ErroInesperado = "erro_inesperado";
}

public static class EtapasFalha
{
    public const string FormatoChave = "formato_chave";
    public const string ListaModelos = "lista_modelos";
    public const string ModeloNaLista = "modelo_na_lista";
    public const string ChamadaModelo = "chamada_modelo";
    public const string Ferramentas = "ferramentas";
    public const string Servidor = "servidor";
}

public static class AcoesFalha
{
    public const string TentarDeNovo = "tentar_de_novo";
    public const string TrocarModelo = "trocar_modelo";
    public const string ColarChave = "colar_chave";
    public const string RemoverVariavel = "remover_variavel";
    public const string AbrirPainelProvedor = "abrir_painel_provedor";
    public const string TrocarProvedor = "trocar_provedor";
    public const string LimitarUso = "limitar_uso";
    public const string CorrigirUrl = "corrigir_url";
    public const string AumentarTokens = "aumentar_tokens";
    public const string BaixarEsforco = "baixar_esforco";
    public const string Religar = "religar";
    public const string VerDetalhes = "ver_detalhes";
    public const string FalarSuporte = "falar_suporte";
}

/// <summary>Uma falha explicada para a Gestão (CONTRATO-API §12.1). <c>Erro</c> antigo = <see cref="Titulo"/>.</summary>
public sealed record FalhaDto(string Codigo, string? Etapa, string Titulo, string? Detalhe, string? Provedor, string? Modelo, int? Status,
    string? CodigoProvedor, string? MensagemProvedor, bool Temporaria, DateTimeOffset? TentarDeNovoEm, IReadOnlyList<string> Acoes,
    IReadOnlyList<string>? ModelosSugeridos);

/// <summary>O que cada código significa: efeito na cadeia, se passa sozinho, título e ações (CONTRATO-API §12.2 e §12.3).</summary>
public sealed record InfoFalha(TipoFalha Tipo, bool Temporaria, string Titulo, IReadOnlyList<string> Acoes);

public static class CatalogoFalhas
{
    private static readonly Dictionary<string, InfoFalha> Info = new()
    {
        [CodigosFalha.SemChave] = new(TipoFalha.SemChave, false, "Sem chave cadastrada", [AcoesFalha.ColarChave]),
        [CodigosFalha.ChaveFormato] = new(TipoFalha.Configuracao, false, "A chave colada não parece certa", [AcoesFalha.ColarChave]),
        [CodigosFalha.ChaveOutroProvedor] = new(TipoFalha.Configuracao, false, "Essa chave parece ser de outro provedor", [AcoesFalha.ColarChave]),
        [CodigosFalha.ChaveRecusada] = new(TipoFalha.Configuracao, false, "Chave recusada pelo provedor", [AcoesFalha.ColarChave]),
        [CodigosFalha.ChaveAmbienteRecusada] = new(TipoFalha.Configuracao, false, "A chave do servidor foi recusada", [AcoesFalha.RemoverVariavel]),
        [CodigosFalha.SemPermissao] = new(TipoFalha.Configuracao, false, "A chave não tem permissão para isso", [AcoesFalha.AbrirPainelProvedor, AcoesFalha.TrocarModelo]),
        [CodigosFalha.RegiaoBloqueada] = new(TipoFalha.Configuracao, false, "O provedor não atende a região do servidor", [AcoesFalha.TrocarProvedor]),
        [CodigosFalha.SemCredito] = new(TipoFalha.Configuracao, false, "A conta do provedor está sem crédito", [AcoesFalha.AbrirPainelProvedor]),
        [CodigosFalha.LimiteMinuto] = new(TipoFalha.Limite, true, "Limite de pedidos por minuto atingido", [AcoesFalha.TentarDeNovo]),
        [CodigosFalha.CotaDia] = new(TipoFalha.Limite, true, "A cota do dia acabou", [AcoesFalha.TrocarModelo, AcoesFalha.LimitarUso]),
        [CodigosFalha.ModeloNaoExiste] = new(TipoFalha.Configuracao, false, "Esse modelo não existe para esta chave", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.ModeloSemAcesso] = new(TipoFalha.Configuracao, false, "A conta não tem acesso a esse modelo", [AcoesFalha.TrocarModelo, AcoesFalha.AbrirPainelProvedor]),
        [CodigosFalha.ModeloNaoConversa] = new(TipoFalha.Configuracao, false, "Esse modelo não é de conversa", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.ModeloSemFerramentas] = new(TipoFalha.Configuracao, false, "Esse modelo não usa ferramentas", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.ParametroNaoSuportado] = new(TipoFalha.Pedido, false, "O modelo recusou um ajuste do pedido", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.PedidoGrande] = new(TipoFalha.Pedido, false, "O pedido ficou grande demais para o modelo", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.PedidoRecusado] = new(TipoFalha.Pedido, false, "O provedor recusou o pedido", [AcoesFalha.VerDetalhes]),
        [CodigosFalha.ModeloSobrecarregado] = new(TipoFalha.Servidor, true, "O modelo está sobrecarregado agora", [AcoesFalha.TentarDeNovo, AcoesFalha.TrocarModelo]),
        [CodigosFalha.ErroInternoProvedor] = new(TipoFalha.Servidor, true, "Erro interno no provedor", [AcoesFalha.TentarDeNovo, AcoesFalha.TrocarModelo]),
        [CodigosFalha.TempoEsgotado] = new(TipoFalha.Servidor, true, "O provedor demorou demais para responder", [AcoesFalha.TentarDeNovo, AcoesFalha.TrocarModelo]),
        [CodigosFalha.SemConexao] = new(TipoFalha.Servidor, true, "Sem conexão com o provedor", [AcoesFalha.TentarDeNovo, AcoesFalha.CorrigirUrl]),
        [CodigosFalha.UrlErrada] = new(TipoFalha.Configuracao, false, "O endereço do provedor parece errado", [AcoesFalha.CorrigirUrl]),
        [CodigosFalha.ConteudoBloqueado] = new(TipoFalha.Resposta, false, "O modelo recusou o conteúdo", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.RespostaVazia] = new(TipoFalha.Resposta, true, "O modelo respondeu vazio", [AcoesFalha.TentarDeNovo, AcoesFalha.TrocarModelo]),
        [CodigosFalha.RespostaCortada] = new(TipoFalha.Resposta, false, "A resposta foi cortada no limite de tokens", [AcoesFalha.AumentarTokens, AcoesFalha.BaixarEsforco]),
        [CodigosFalha.FerramentaErrada] = new(TipoFalha.Resposta, false, "O modelo errou as ferramentas duas vezes", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.VoltasDemais] = new(TipoFalha.Resposta, false, "O modelo usou ferramentas demais", [AcoesFalha.TrocarModelo]),
        [CodigosFalha.CifraIndisponivel] = new(TipoFalha.Configuracao, false, "O servidor não consegue guardar chaves", [AcoesFalha.FalarSuporte]),
        [CodigosFalha.ErroInesperado] = new(TipoFalha.Pedido, false, "Erro inesperado", [AcoesFalha.VerDetalhes, AcoesFalha.TentarDeNovo]),
    };

    public static IReadOnlyCollection<string> Codigos => Info.Keys;

    public static InfoFalha De(string codigo) => Info.GetValueOrDefault(codigo) ?? Info[CodigosFalha.ErroInesperado];

    /// <summary>Exceção pronta para um código do catálogo (mensagem = título).</summary>
    public static FalhaProvedorException Nova(string provedor, string codigo, int? status = null, string? codigoProvedor = null, string? mensagemProvedor = null,
        TimeSpan? retry = null, Exception? inner = null, string? etapa = null)
    {
        var i = De(codigo);
        return new FalhaProvedorException(provedor, i.Titulo, i.Tipo, inner, retry, codigo == CodigosFalha.CotaDia, status, codigo, codigoProvedor,
            Redacao.Limpar(mensagemProvedor)) { Etapa = etapa };
    }

    private static readonly string[] SinaisCotaDia = ["per day", "per-day", "perday", "daily", "rpd", "requests per day", "free-models-per-day", "tokens per day"];
    private static readonly string[] SinaisChave = ["api_key_invalid", "api key not valid", "invalid api key", "incorrect api key", "invalid x-api-key",
        "invalid_api_key", "invalid authentication", "authentication_error", "invalid_authentication", "no auth credentials", "unauthenticated"];
    private static readonly string[] SinaisCredito = ["insufficient_quota", "credit balance", "insufficient credits", "billing", "payment required", "exceeded your current quota"];
    private static readonly string[] SinaisRegiao = ["location is not supported", "unsupported_country", "user location", "not available in your country", "region is not supported"];
    private static readonly string[] SinaisGrande = ["context length", "context_length", "maximum context", "too many tokens", "request_too_large", "prompt is too long", "context window"];
    private static readonly string[] SinaisSemFerramentas = ["does not support tools", "tool use is not supported", "tools are not supported", "support tool use",
        "function calling is not enabled", "does not support function", "tool calling is not supported", "tool_use is not supported"];
    private static readonly string[] SinaisSemAcesso = ["do not have access", "does not have access", "not available on your", "not have permission to use", "requires a paid", "not enabled for your"];
    private static readonly string[] SinaisNaoConversa = ["only supports audio", "response modalities", "is not supported for generatecontent", "not a chat model",
        "does not support chat", "not supported in the v1/chat/completions", "only supports text-to-speech", "embedding model"];
    private static readonly string[] SinaisNaoExiste = ["model_not_found", "is not a valid model", "does not exist", "unknown model", "model not found",
        "no such model", "is not found for api version", "invalid model"];
    private static readonly string[] SinaisSobrecarga = ["overloaded", "unavailable", "high demand", "capacity", "try again later"];
    private static readonly string[] SinaisBloqueio = ["safety", "content_filter", "blockreason", "content policy", "prohibited_content"];

    private static bool Tem(string texto, string[] sinais) => sinais.Any(s => texto.Contains(s, StringComparison.Ordinal));

    /// <summary>Status HTTP + corpo → código do §12.2. O corpo vence quando é mais específico que o status.</summary>
    public static string Classificar(int status, string? codigoProvedor, string? mensagem, string? etapa)
    {
        var t = $"{codigoProvedor} {mensagem}".ToLowerInvariant();
        if (status == 0) return CodigosFalha.SemConexao;
        if (Tem(t, SinaisChave) || status == 401) return CodigosFalha.ChaveRecusada;
        if (status == 402 || Tem(t, SinaisCredito)) return CodigosFalha.SemCredito;
        if (Tem(t, SinaisRegiao)) return CodigosFalha.RegiaoBloqueada;
        if (status == 429) return Tem(t, SinaisCotaDia) || t.Contains("resource_exhausted") && t.Contains("quota") ? CodigosFalha.CotaDia : CodigosFalha.LimiteMinuto;
        if (status == 413 || Tem(t, SinaisGrande)) return CodigosFalha.PedidoGrande;
        if (Tem(t, SinaisSemFerramentas)) return CodigosFalha.ModeloSemFerramentas;
        if (Tem(t, SinaisSemAcesso)) return CodigosFalha.ModeloSemAcesso;
        if (Tem(t, SinaisNaoConversa)) return CodigosFalha.ModeloNaoConversa;
        if (etapa == EtapasFalha.ListaModelos && status is 404 or 405) return CodigosFalha.UrlErrada;
        if (status == 404 || Tem(t, SinaisNaoExiste)) return CodigosFalha.ModeloNaoExiste;
        if (status == 403 || t.Contains("permission_denied")) return CodigosFalha.SemPermissao;
        if (t.Contains("failed_precondition")) return CodigosFalha.RegiaoBloqueada;
        if (status is 503 or 529 || status >= 500 && Tem(t, SinaisSobrecarga)) return CodigosFalha.ModeloSobrecarregado;
        if (status >= 500) return CodigosFalha.ErroInternoProvedor;
        if (status is 400 or 422 && Tem(t, SinaisBloqueio)) return CodigosFalha.ConteudoBloqueado;
        if (status == 405) return CodigosFalha.UrlErrada;
        return CodigosFalha.PedidoRecusado;
    }

    /// <summary>Monta a falha da tela. A chave em uso nunca entra; <paramref name="sugeridos"/> só vai se houver "trocar modelo".</summary>
    public static FalhaDto Dto(FalhaProvedorException f, string? nomeProvedor, string? modelo, DateTimeOffset agora, string? origemChave = null,
        IReadOnlyList<string>? sugeridos = null, string? etapa = null)
    {
        var codigo = f.Codigo;
        // Chave recusada vinda da variável do servidor: colar outra pela tela não adianta.
        if (codigo == CodigosFalha.ChaveRecusada && origemChave == OrigensChave.Ambiente) codigo = CodigosFalha.ChaveAmbienteRecusada;
        var info = De(codigo);
        var et = etapa ?? f.Etapa;
        var titulo = codigo == f.Codigo ? f.Message : info.Titulo;
        DateTimeOffset? quando = f.CotaDiaria || f.RetryAfter is not null ? RegrasCadeia.EsgotadoAte(f, agora) : null;
        var comTroca = info.Acoes.Contains(AcoesFalha.TrocarModelo);
        var sug = comTroca && sugeridos is { Count: > 0 } ? sugeridos.Where(s => s != modelo).Take(4).ToList() : null;
        return new FalhaDto(codigo, et, titulo, Detalhe(codigo, et, nomeProvedor ?? f.Provedor, modelo, f.Status), f.Provedor, modelo, f.Status,
            f.CodigoProvedor, f.MensagemProvedor, info.Temporaria, quando, info.Acoes, sug is { Count: > 0 } ? sug : null);
    }

    /// <summary>Falha sem exceção (validação local, sem chave, cifra).</summary>
    public static FalhaDto Dto(string codigo, string provedor, string? nomeProvedor, string? modelo, string? etapa, string? detalhe = null)
    {
        var d = Dto(Nova(provedor, codigo, etapa: etapa), nomeProvedor, modelo, DateTimeOffset.UtcNow);
        return detalhe is null ? d : d with { Detalhe = detalhe };
    }

    /// <summary>1–2 frases: o que aconteceu e o que já se sabe que está bem.</summary>
    public static string Detalhe(string codigo, string? etapa, string provedor, string? modelo, int? status)
    {
        var m = string.IsNullOrWhiteSpace(modelo) ? "o modelo" : modelo;
        var chaveOk = etapa is EtapasFalha.ChamadaModelo or EtapasFalha.Ferramentas or EtapasFalha.ModeloNaLista ? "A chave funcionou; quem falhou foi o modelo. " : "";
        var st = status is int s and > 0 ? $" ({s})" : "";
        return codigo switch
        {
            CodigosFalha.SemChave => $"{provedor} não tem chave nem na tela nem no servidor. Cole uma chave para ligar.",
            CodigosFalha.ChaveFormato => "Tem espaço, quebra de linha, aspas ou o tamanho não bate. Copie a chave de novo, inteira.",
            CodigosFalha.ChaveOutroProvedor => $"O começo da chave lembra outro provedor, não {provedor}. Confira se copiou a certa.",
            CodigosFalha.ChaveRecusada => $"{provedor} disse que a chave não vale{st}. Pode ter sido apagada, digitada errada ou ser de outra conta.",
            CodigosFalha.ChaveAmbienteRecusada => $"A chave que está valendo é a da variável do servidor, e {provedor} recusou{st}. Enquanto ela existir, colar outra aqui não muda nada.",
            CodigosFalha.SemPermissao => $"{provedor} aceitou a chave, mas negou o acesso{st}. Ative a API no projeto ou libere o modelo na conta.",
            CodigosFalha.RegiaoBloqueada => $"{provedor} não atende o país onde o servidor roda. Use outro provedor.",
            CodigosFalha.SemCredito => $"{provedor} recusou por falta de crédito ou de plano pago{st}. Coloque crédito no painel do provedor; esperar não resolve.",
            CodigosFalha.LimiteMinuto => $"{chaveOk}{provedor} pediu para esperar{st}. Passa sozinho em instantes.",
            CodigosFalha.CotaDia => $"{chaveOk}{provedor} não aceita mais pedidos de {m} hoje{st}. Volta quando a cota zerar; até lá, outro modelo pode responder.",
            CodigosFalha.ModeloNaoExiste => $"{(chaveOk.Length > 0 ? "A chave funcionou. " : "")}{provedor} não conhece {m}{st}. Escolha um da lista.",
            CodigosFalha.ModeloSemAcesso => $"{provedor} reconhece {m}, mas esta conta não pode usar{st}. Troque o modelo ou libere na conta.",
            CodigosFalha.ModeloNaoConversa => $"{m} é de voz, imagem, vídeo ou busca, não de conversa. Escolha um modelo de conversa.",
            CodigosFalha.ModeloSemFerramentas => $"{m} não aceita as ferramentas que o assistente usa{st}. Escolha outro modelo.",
            CodigosFalha.ParametroNaoSuportado => $"{m} recusou um ajuste do pedido mesmo depois de o servidor tentar sem ele{st}.",
            CodigosFalha.PedidoGrande => $"A conversa passou do tamanho que {m} aceita{st}. Um modelo com janela maior resolve.",
            CodigosFalha.PedidoRecusado => $"{provedor} recusou o pedido{st}. Veja a mensagem do provedor nos detalhes.",
            CodigosFalha.ModeloSobrecarregado => $"{provedor} respondeu{st} que {m} está sem capacidade agora. {chaveOk}Costuma passar em minutos; se repetir, troque o modelo.",
            CodigosFalha.ErroInternoProvedor => $"{provedor} teve um erro do lado dele{st}. {chaveOk}Tente de novo daqui a pouco.",
            CodigosFalha.TempoEsgotado => $"{provedor} não respondeu a tempo. {chaveOk}Pode ser lentidão passageira ou um modelo pesado demais.",
            CodigosFalha.SemConexao => $"O servidor não conseguiu falar com {provedor} (rede, DNS ou certificado). Se o provedor foi criado na tela, confira o endereço.",
            CodigosFalha.UrlErrada => $"O endereço de {provedor} não respondeu como uma API{st}. Costuma faltar o /v1 no fim.",
            CodigosFalha.ConteudoBloqueado => $"{chaveOk}{m} recusou responder por regra de conteúdo.",
            CodigosFalha.RespostaVazia => $"{chaveOk}{m} respondeu sem texto e sem usar ferramenta.",
            CodigosFalha.RespostaCortada => $"{chaveOk}{m} gastou todo o limite de tokens antes de terminar. Modelos que pensam antes de responder precisam de mais tokens ou de esforço menor.",
            CodigosFalha.FerramentaErrada => $"{chaveOk}{m} chamou as ferramentas do jeito errado duas vezes seguidas.",
            CodigosFalha.VoltasDemais => $"{chaveOk}{m} ficou chamando ferramentas sem terminar a resposta.",
            CodigosFalha.CifraIndisponivel => "Falta a configuração de cifra no servidor (Atendimento:ChaveToken). Só o suporte resolve.",
            _ => $"Algo inesperado aconteceu com {provedor}{st}. Os detalhes técnicos ajudam o suporte.",
        };
    }
}

/// <summary>Lê o corpo de erro dos provedores (OpenAI, Gemini, Groq, OpenRouter, Anthropic): código e mensagem. Público para teste.</summary>
public static class CorpoErro
{
    private static readonly Regex RetryDelay = new("\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"", RegexOptions.Compiled);

    public static (string? Codigo, string? Mensagem) Ler(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (null, null);
        var inicio = texto.IndexOfAny(['{', '[']);
        if (inicio >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto[inicio..]);
                var raiz = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
                var erro = raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("error", out var e) ? e : raiz;
                if (erro.ValueKind == JsonValueKind.String) return (null, erro.GetString());
                if (erro.ValueKind == JsonValueKind.Object)
                {
                    string? Txt(string nome) => erro.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var codigo = Txt("status") ?? Txt("code") ?? Txt("type");
                    // Gemini põe o motivo fino em details[].reason (ex.: API_KEY_INVALID).
                    if (erro.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Array)
                        foreach (var d in det.EnumerateArray())
                            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                                codigo = codigo is null ? r.GetString() : $"{codigo} {r.GetString()}";
                    var msg = Txt("message");
                    // OpenRouter guarda a resposta crua do provedor de baixo em metadata.raw.
                    if (erro.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
                        msg = $"{msg} {raw.GetString()}".Trim();
                    if (codigo is not null || msg is not null) return (codigo, msg);
                }
            }
            catch (JsonException) { /* não era JSON inteiro: vai como texto */ }
        }
        return (null, texto.Trim());
    }

    /// <summary>Corpo que é página HTML (endereço errado, proxy, login), não API.</summary>
    public static bool EhHtml(string? texto) => texto is not null && (texto.Contains("<html", StringComparison.OrdinalIgnoreCase) || texto.Contains("<!doctype", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gemini manda a espera no corpo (RetryInfo.retryDelay) em vez do cabeçalho.</summary>
    public static TimeSpan? Espera(string? texto) =>
        texto is not null && RetryDelay.Match(texto) is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s) : null;
}

/// <summary>Tira chaves de qualquer texto que vai para a tela ou o log. Público para teste.</summary>
public static class Redacao
{
    private static readonly Regex Chaves = new(
        @"(sk-ant-[\w\-]{6,}|sk-or-[\w\-]{6,}|sk-proj-[\w\-]{6,}|sk-[\w\-]{16,}|gsk_[\w\-]{6,}|AIza[\w\-]{16,}|AQ\.[\w\-\.]{16,}|(?<=key=)[^&\s""']+|(?<=Bearer )[\w\-\.]{8,})",
        RegexOptions.Compiled);

    public static string? Limpar(string? texto, string? chave = null)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = string.IsNullOrEmpty(chave) ? texto : texto.Replace(chave, "•••", StringComparison.Ordinal);
        t = Chaves.Replace(t, "•••").Trim();
        return MensagensFalha.Curta(t);
    }
}

/// <summary>Tipo de cada modelo, estável ou não, e os que a gente recomenda (CONTRATO-API §12.4). Público para teste.</summary>
public static class CatalogoModelos
{
    public const string Conversa = "conversa";

    private static readonly (string Tipo, string[] Sinais)[] Tipos =
    [
        ("embedding", ["embedding", "embed", "aqa"]),
        ("voz", ["tts", "audio", "transcribe", "whisper", "native-audio", "-live", "live-", "realtime", "speech"]),
        ("imagem", ["image", "imagen", "nano-banana", "dall-e", "gpt-image"]),
        ("video", ["veo", "genie", "sora"]),
        ("musica", ["lyria"]),
        ("outro", ["robotics", "computer-use", "deep-research", "antigravity", "moderation", "guard", "davinci", "babbage", "search"]),
    ];

    /// <summary>Recomendados por provedor: escolha nossa, conferida em 25/11/2026 (Gemini: o 2.5 Flash respondeu na chave grátis; o 3.8 deu 503).</summary>
    private static readonly Dictionary<string, string[]> Recomendados = new()
    {
        [Provedores.Gemini] = ["gemini-2.5-flash", "gemini-3.5-flash"],
        [Provedores.Groq] = ["llama-3.3-70b-versatile"],
        [Provedores.OpenAI] = ["gpt-5.4-mini"],
        [Provedores.Anthropic] = ["claude-haiku-5-5", "claude-sonnet-5-5"],
    };

    /// <summary>Tira o prefixo <c>models/</c> do Gemini compatível.</summary>
    public static string Normalizar(string id) => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id;

    public static string Tipo(string id)
    {
        var m = id.ToLowerInvariant();
        foreach (var (tipo, sinais) in Tipos)
            if (sinais.Any(s => m.Contains(s, StringComparison.Ordinal))) return tipo;
        return Conversa;
    }

    private static readonly Regex Data = new(@"(\d{4}-\d{2}(-\d{2})?|-\d{2}-\d{4}|-\d{4}$|-\d{2}-\d{2}$)", RegexOptions.Compiled);

    public static bool Estavel(string id)
    {
        var m = id.ToLowerInvariant();
        return !m.Contains("preview") && !m.Contains("exp") && !m.Contains("-latest") && !m.Contains("beta") && !m.Contains("alpha") && !Data.IsMatch(m);
    }

    public static bool Recomendado(string provedor, string id) => Recomendados.TryGetValue(provedor, out var r) && r.Contains(id);

    /// <summary>Lista ordenada para a tela: recomendados, conversa estável, conversa instável, o resto.</summary>
    public static IReadOnlyList<ModeloInfoDto> Montar(string provedor, IEnumerable<string> ids) => ids
        .Select(Normalizar).Distinct()
        .Select(id => new ModeloInfoDto(id, null, Tipo(id), Estavel(id), Recomendado(provedor, id)))
        .OrderBy(m => m.Recomendado ? 0 : m.Tipo == Conversa ? (m.Estavel ? 1 : 2) : 3).ThenBy(m => m.Id, StringComparer.Ordinal)
        .ToList();

    /// <summary>Até 4 modelos de conversa para trocar, preferindo recomendados e estáveis.</summary>
    public static IReadOnlyList<string> Sugeridos(string provedor, IEnumerable<string> ids, string? atual) => Montar(provedor, ids)
        .Where(m => m.Tipo == Conversa && m.Id != atual && (m.Estavel || m.Recomendado)).Take(4).Select(m => m.Id).ToList();

    /// <summary>Prefixo típico de cada provedor. Nulo = não dá para dizer.</summary>
    public static string? ProvedorDaChave(string chave) =>
        chave.StartsWith("sk-ant-", StringComparison.Ordinal) ? Provedores.Anthropic
        : chave.StartsWith("sk-or-", StringComparison.Ordinal) ? Provedores.OpenRouter
        : chave.StartsWith("gsk_", StringComparison.Ordinal) ? Provedores.Groq
        : chave.StartsWith("AIza", StringComparison.Ordinal) || chave.StartsWith("AQ.", StringComparison.Ordinal) ? Provedores.Gemini
        : chave.StartsWith("sk-", StringComparison.Ordinal) ? Provedores.OpenAI
        : null;
}

public sealed record ModeloInfoDto(string Id, string? Nome, string Tipo, bool Estavel, bool Recomendado);
