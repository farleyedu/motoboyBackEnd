using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Social.Nucleo;

namespace APIBack.Social.Fila;

/*
 * Zippy Social R14: regras puras da fila e das máquinas (testáveis sem banco).
 * Contrato: zippy-admin/docs/zippy-social/prompts/editor-real/R14-CONTRATO-FILA.md. O backend nunca processa vídeo.
 */

/// <summary>O arquivo original do corte, que já está no PC. O programa o acha por nome e tamanho.</summary>
public sealed record OriginalDto(long? VideoId, bool Exemplo, string? Nome, long? Bytes, decimal Ini);

public static class FilaRegras
{
    public const string TipoRenderizar = "renderizar";
    public const int MaxTentativas = 3;
    public const int EsperaMaximaS = 25;
    public static readonly TimeSpan ValidadeCodigo = TimeSpan.FromMinutes(10);
    /// <summary>Máquina "ligada" na tela: sinal há menos disto.</summary>
    public static readonly TimeSpan Online = TimeSpan.FromSeconds(90);
    /// <summary>Trabalho rodando sem sinal por este tempo volta para a fila.</summary>
    public static readonly TimeSpan SemSinal = TimeSpan.FromMinutes(2);
    /// <summary>Um render longo numa CPU fraca pode demorar; a URL é assinada na entrega e baixada antes de começar.</summary>
    public static readonly TimeSpan ValidadeArquivo = TimeSpan.FromHours(1);

    public static string NovoCodigo() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string NovoToken() =>
        "zsm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string Codigo(string? codigo)
    {
        var c = (codigo ?? "").Replace(" ", "").Replace("-", "");
        return c.Length == 6 && c.All(char.IsAsciiDigit) ? c : throw CodigoInvalido();
    }

    public static SocialException CodigoInvalido() =>
        new("CODIGO_INVALIDO", "Código errado ou vencido. Gere um novo no painel.", 400);

    public static SocialException NaoAutorizada() =>
        new("MAQUINA_NAO_AUTORIZADA", "Esta máquina não está autorizada. Ligue-a de novo pelo painel.", 401);

    public static SocialException Limite(int limite) =>
        new("LIMITE_MAQUINAS", $"Seu plano tem {limite} {(limite == 1 ? "máquina" : "máquinas")}. Remova uma ou mude de plano.", 409);

    public static string DispositivoId(string? id)
    {
        var v = (id ?? "").Trim();
        return v.Length is >= 8 and <= 80 ? v : throw SocialException.Validacao("dispositivoId inválido.");
    }

    public static string Nome(string? nome, string padrao = "PC")
    {
        var v = Curto(nome, 60);
        return v ?? padrao;
    }

    /// <summary>Texto vindo da máquina: sem controle, aparado no limite; vazio vira null.</summary>
    public static string? Curto(string? texto, int max)
    {
        var v = new string((texto ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (v.Length == 0) return null;
        return v.Length > max ? v[..max] : v;
    }

    public static bool EstaOnline(DateTime? ultimoSinal, DateTime agora) => ultimoSinal is { } s && agora - s < Online;

    /// <summary>Usa a máquina quem é dono dela, ou qualquer pessoa da loja quando ela está compartilhada.</summary>
    public static bool PodeUsar(int dono, bool compartilhada, int usuario) => dono == usuario || compartilhada;

    public static string Tema(string? tema) => tema == "gta6" ? "gta6" : "padrao";

    /// <summary>Depois de uma falha (ou de a máquina sumir): volta para a fila ou falha de vez.</summary>
    public static bool VaiRepetir(bool podeRepetir, int tentativas) => podeRepetir && tentativas < MaxTentativas;

    public static string ChaveIdempotencia(long corteId, int versao, long maquinaId, string destino, string qualidade) =>
        $"renderizar:{corteId}:{versao}:{maquinaId}:{destino}:{qualidade}";

    /// <summary>
    /// Os dados do corte como o navegador os usa (<c>dadosDoCorte</c> em <c>editor/estado/carregar.ts</c>): completa o que falta em
    /// <c>social_cortes.dados</c>, para a máquina gerar o mesmo vídeo.
    /// </summary>
    public static JsonObject Dados(long corteId, decimal ini, decimal fim, decimal? momentoForte, string? dadosJson)
    {
        var dados = new JsonObject
        {
            ["id"] = corteId.ToString(),
            ["dur"] = fim - ini,
            ["ext"] = new JsonObject { ["antes"] = 0, ["depois"] = 0 },
            ["marks"] = new JsonArray(),
            ["falas"] = new JsonArray()
        };
        var salvos = string.IsNullOrWhiteSpace(dadosJson) ? null : JsonNode.Parse(dadosJson) as JsonObject;
        if (salvos?["marks"] is JsonArray)
        {
            foreach (var (chave, valor) in salvos)
                if (valor is not null) dados[chave] = valor.DeepClone();
            dados["id"] = corteId.ToString();
        }
        if (dados["forte"] is null && momentoForte is { } forte) dados["forte"] = forte;
        return dados;
    }

    /// <summary>Todo <c>arquivoId</c> citado em qualquer lugar da receita (marca d'água, camadas, falas, sons).</summary>
    public static IReadOnlyList<Guid> ArquivosDaReceita(JsonElement receita)
    {
        var achados = new List<Guid>();
        void Andar(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "arquivoId" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        if (Guid.TryParse(p.Value.GetString(), out var id) && !achados.Contains(id)) achados.Add(id);
                    }
                    else Andar(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) Andar(item);
        }
        Andar(receita);
        return achados;
    }

    /// <summary>
    /// O original do pedido: vídeo, exemplo e início vêm do banco; só nome e tamanho vêm do navegador (ele é quem tem o arquivo).
    /// </summary>
    public static OriginalDto Original(long videoId, bool exemplo, decimal ini, string? nome, long? bytes)
    {
        var n = Curto(nome, 255);
        if (n is not null && n.IndexOfAny(['/', '\\']) >= 0) throw SocialException.Validacao("O nome do original não pode ter pasta.");
        return new OriginalDto(videoId, exemplo, n, bytes is > 0 ? bytes : null, ini);
    }
}
