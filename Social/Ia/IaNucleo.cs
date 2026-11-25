using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.Social.Nucleo;

namespace APIBack.Social.Ia;

/// <summary>Usos de IA (coluna <c>uso</c> de <c>social_config_ia</c> e <c>social_uso_ia</c>). A fase 2 acrescenta os outros.</summary>
public static class UsosIa
{
    public const string Voz = "voz", VozClone = "voz_clone", Roteiro = "roteiro";
    public const string Textos = "textos", Transcricao = "transcricao", Analise = "analise", Traducao = "traducao", Objetivo = "objetivo";
}

public static class OrigensIa
{
    public const string Zippy = "zippy", Propria = "propria", Local = "local";
}

public static class AoFalharIa
{
    public const string UsarCota = "usar_cota", Local = "local", Parar = "parar";
}

public static class ProvedoresSocial
{
    public const string ElevenLabs = "elevenlabs", OpenAI = "openai", Azure = "azure_speech", Anthropic = "anthropic";
    /// <summary>Só em desenvolvimento e nos testes: áudio em silêncio e texto de exemplo.</summary>
    public const string Falso = "falso";
}

/// <summary>
/// Provedor que vai atender um pedido, já com a chave aberta (nunca sai do backend).
/// <see cref="Modelos"/> e <see cref="Precos"/> vêm do catálogo <c>social_provedores</c>.
/// </summary>
public sealed record ProvedorEscolhido(
    string ProvedorId, string Origem, string? Chave, string? BaseUrl, string? Modelo, JsonElement Modelos, JsonElement Precos,
    string AoFalhar, long? ChaveId = null)
{
    public bool Falso => ProvedorId == ProvedoresSocial.Falso;

    /// <summary>Id da voz do provedor para uma das 6 vozes do protótipo (<c>modelos.vozes</c>). Sem mapa: usa o próprio nome.</summary>
    public string VozDoProvedor(string voz) =>
        Modelos.ValueKind == JsonValueKind.Object && Modelos.TryGetProperty("vozes", out var vozes) &&
        vozes.ValueKind == JsonValueKind.Object && vozes.TryGetProperty(voz, out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : voz;

    public string? ModeloDe(string chave) =>
        Modelo ?? (Modelos.ValueKind == JsonValueKind.Object && Modelos.TryGetProperty(chave, out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null);

    public decimal Preco(string chave) =>
        Precos.ValueKind == JsonValueKind.Object && Precos.TryGetProperty(chave, out var p) && p.TryGetDecimal(out var v) ? v : 0m;
}

/// <summary>Por que a chamada ao provedor falhou. Decide se a chave própria fica marcada e se cai para a cota da Zippy.</summary>
public enum TipoFalhaIa
{
    /// <summary>401/403: chave recusada.</summary>
    ChaveRecusada,
    /// <summary>402 ou cota do provedor acabou.</summary>
    SemSaldo,
    /// <summary>429 do provedor.</summary>
    Limite,
    /// <summary>5xx, rede, tempo esgotado.</summary>
    ForaDoAr,
    /// <summary>O provedor recusou o pedido (texto, voz, formato). Trocar de provedor não resolve.</summary>
    Pedido,
    /// <summary>Sem chave configurada.</summary>
    SemChave,
}

public sealed class FalhaIaException(string provedor, string mensagem, TipoFalhaIa tipo, Exception? inner = null)
    : Exception(mensagem, inner)
{
    public string Provedor { get; } = provedor;
    public TipoFalhaIa Tipo { get; } = tipo;

    /// <summary>Falha que outra chave/provedor pode resolver (tudo menos pedido inválido).</summary>
    public bool TrocarResolve => Tipo != TipoFalhaIa.Pedido;

    public static FalhaIaException DeHttp(string provedor, int status, string? corpo)
    {
        var tipo = status switch
        {
            401 or 403 => TipoFalhaIa.ChaveRecusada,
            402 => TipoFalhaIa.SemSaldo,
            429 when corpo?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true => TipoFalhaIa.SemSaldo,
            429 => TipoFalhaIa.Limite,
            >= 500 => TipoFalhaIa.ForaDoAr,
            _ => TipoFalhaIa.Pedido,
        };
        return new FalhaIaException(provedor, MensagemDe(tipo), tipo);
    }

    public static string MensagemDe(TipoFalhaIa tipo) => tipo switch
    {
        TipoFalhaIa.ChaveRecusada => "chave recusada",
        TipoFalhaIa.SemSaldo => "sem saldo no provedor",
        TipoFalhaIa.Limite => "limite de uso do provedor",
        TipoFalhaIa.ForaDoAr => "provedor fora do ar",
        TipoFalhaIa.SemChave => "sem chave configurada",
        _ => "o provedor recusou o pedido",
    };
}

/// <summary>Erros do Social específicos de IA e créditos (mesmo formato de <see cref="SocialException"/>).</summary>
public static class IaErros
{
    public static SocialException SemCreditos(int precisa, int restam) => new("SEM_CREDITOS",
        $"Faltam {precisa - restam} créditos: precisa de {precisa}, restam {restam}.", 402)
    {
        Detalhes = new { precisa, restam, falta = precisa - restam },
    };

    public static SocialException ContaEmAtraso() => new("CONTA_EM_ATRASO",
        "A cota de IA da Zippy está pausada até a fatura em aberto ser paga. Dá para usar a sua própria chave.", 402);

    public static SocialException LimiteMinuto() => new("LIMITE_IA",
        "Muitos pedidos de IA em pouco tempo. Espere um minuto e tente de novo.", 429);

    public static SocialException TetoDiario(int teto) => new("TETO_DIARIO_IA",
        $"Esta loja chegou ao limite de {teto} créditos de IA por dia na cota da Zippy. Volta amanhã ou use a sua própria chave.", 429);

    public static SocialException ChavePropriaFalhou(string provedor, TipoFalhaIa tipo) => new("CHAVE_PROPRIA_FALHOU",
        $"Sua chave do {NomeProvedor(provedor)} falhou ({FalhaIaException.MensagemDe(tipo)}). Troque a chave ou use a cota da Zippy.", 502)
    {
        Detalhes = new { provedor, motivo = tipo.ToString() },
    };

    public static SocialException ProvedorFalhou(string provedor, TipoFalhaIa tipo) => tipo == TipoFalhaIa.Pedido
        ? new SocialException("PEDIDO_RECUSADO", $"O {NomeProvedor(provedor)} recusou o pedido. Confira o texto e tente de novo.", 422)
        : new SocialException("IA_INDISPONIVEL",
            $"O serviço de IA ({NomeProvedor(provedor)}) não respondeu agora ({FalhaIaException.MensagemDe(tipo)}). Tente de novo em instantes.", 503)
        {
            Detalhes = new { provedor, motivo = tipo.ToString() },
        };

    public static SocialException SemProvedor(string uso) => new("IA_NAO_CONFIGURADA",
        uso == UsosIa.VozClone
            ? "A voz clonada ainda não foi configurada nesta instalação."
            : "A IA ainda não foi configurada nesta instalação (falta a chave do provedor).", 503);

    public static string NomeProvedor(string id) => id switch
    {
        ProvedoresSocial.ElevenLabs => "ElevenLabs",
        ProvedoresSocial.OpenAI => "OpenAI",
        ProvedoresSocial.Azure => "Azure Speech",
        ProvedoresSocial.Anthropic => "Claude",
        ProvedoresSocial.Falso => "provedor de teste",
        _ => id,
    };
}

/// <summary>Contas de crédito puras (sem banco). Os números vêm de <c>SocialIaOptions</c>.</summary>
public static class CreditosRegras
{
    /// <summary>Fala em português, voz da IA na velocidade 1: ~2,6 palavras por segundo.</summary>
    public const decimal PalavrasPorSegundo = 2.6m;

    /// <summary>1 crédito a cada <paramref name="segundosPorCredito"/> segundos (arredonda para cima). Zero segundos = zero.</summary>
    public static int Voz(decimal segundos, int segundosPorCredito)
    {
        if (segundos <= 0) return 0;
        if (segundosPorCredito <= 0) throw new ArgumentOutOfRangeException(nameof(segundosPorCredito));
        return (int)Math.Ceiling(segundos / segundosPorCredito);
    }

    public static int Palavras(string texto) =>
        texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>Duração esperada da fala antes de gerar (para o custo mostrado antes). Mínimo de meio segundo.</summary>
    public static decimal SegundosEstimados(string texto, decimal velocidade)
    {
        var vel = velocidade <= 0 ? 1 : velocidade;
        var s = Palavras(texto) / (PalavrasPorSegundo * vel);
        return Math.Round(Math.Max(0.5m, s), 2);
    }

    /// <summary>Hash do cache de voz: o mesmo texto, voz, velocidade, provedor e modelo gera o mesmo áudio e não cobra de novo.</summary>
    public static string HashFala(string texto, string voz, decimal velocidade, string provedor, string? modelo)
    {
        var chave = string.Join('\u001f', texto.Trim(), voz, velocidade.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            provedor, modelo ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chave))).ToLowerInvariant();
    }
}

/// <summary>Saldo do mês. <see cref="Restam"/> nunca é negativo.</summary>
public sealed record SaldoCreditos(int Concedidos, int Comprados, int Usados)
{
    public int Total => Concedidos + Comprados;
    public int Restam => Math.Max(0, Total - Usados);
    /// <summary>Passou de 80% da cota do mês.</summary>
    public bool Acima80 => Total > 0 && Usados * 5 >= Total * 4;
}

/// <summary>Uma linha de <c>social_uso_ia</c>.</summary>
public sealed record UsoIa(
    string Uso, string? Provedor, string? Modelo, string Origem, decimal Unidades, string Unidade, int Creditos, decimal CustoEstimado,
    long? CorteId = null, object? Detalhe = null);

/// <summary>
/// Proteção da chave da Zippy: chamadas por minuto por loja (em memória, por instância). O teto diário fica no banco
/// (<see cref="CreditosBanco.CreditosZippyHoje"/>), que vale para todas as instâncias.
/// </summary>
public sealed class LimiteIaZippy(TimeProvider relogio)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _chamadas = new();

    public bool Tentar(Guid loja, int porMinuto)
    {
        if (porMinuto <= 0) return true;
        var agora = relogio.GetUtcNow();
        var fila = _chamadas.GetOrAdd(loja, _ => new Queue<DateTimeOffset>());
        lock (fila)
        {
            while (fila.Count > 0 && agora - fila.Peek() >= TimeSpan.FromMinutes(1)) fila.Dequeue();
            if (fila.Count >= porMinuto) return false;
            fila.Enqueue(agora);
            return true;
        }
    }
}
