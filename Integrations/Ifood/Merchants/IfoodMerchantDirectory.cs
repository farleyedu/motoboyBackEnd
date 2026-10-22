using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Merchants;

/// <param name="Code">FOUND, NOT_FOUND, DISABLED, APP_CREDENTIALS, RATE_LIMIT, UNAVAILABLE.</param>
public sealed record IfoodMerchantLookup(string Code, Guid MerchantId, string Message)
{
    public bool Found => Code == "FOUND";
}

public interface IIfoodMerchantDirectory
{
    /// <summary>
    /// Procura, entre as lojas que ja autorizaram o app da Zippy no iFood, a que tem este CNPJ.
    /// So devolve o que o iFood respondeu; nunca adivinha uma loja.
    /// </summary>
    Task<IfoodMerchantLookup> FindByCnpjAsync(string cnpjDigits, CancellationToken ct);
}

public sealed class IfoodMerchantDirectory : IIfoodMerchantDirectory
{
    private const int MaxMerchants = 100;
    private static readonly Uri ListEndpoint = new("https://merchant-api.ifood.com.br/merchant/v1.0/merchants");
    private readonly IHttpClientFactory _http;
    private readonly IIfoodTokenProvider _tokens;
    private readonly IOptionsMonitor<IfoodOptions> _options;

    public IfoodMerchantDirectory(IHttpClientFactory http, IIfoodTokenProvider tokens, IOptionsMonitor<IfoodOptions> options)
        => (_http, _tokens, _options) = (http, tokens, options);

    public async Task<IfoodMerchantLookup> FindByCnpjAsync(string cnpjDigits, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled) return new("DISABLED", Guid.Empty, "A integração com o iFood está desligada no momento.");
        IfoodAccessToken token;
        try { token = await _tokens.GetAsync(ct); }
        catch (IfoodIntegrationException) { return new("APP_CREDENTIALS", Guid.Empty, "A Zippy não conseguiu entrar no iFood agora. Avise a Zippy."); }
        try
        {
            using var client = _http.CreateClient(IfoodMerchantProbe.ClientName);
            var (status, listBody) = await GetAsync(client, ListEndpoint, token, ct);
            if (Failure(status, token) is { } failed) return failed;
            foreach (var id in ListMerchants(listBody).Take(MaxMerchants))
            {
                var (detailStatus, detailBody) = await GetAsync(client, new Uri($"{ListEndpoint}/{id:D}"), token, ct);
                if (detailStatus is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) continue;
                if (Failure(detailStatus, token) is { } detailFailed) return detailFailed;
                if (DocumentMatches(detailBody, cnpjDigits)) return new("FOUND", id, "");
            }
            return new("NOT_FOUND", Guid.Empty, "O iFood não mostrou nenhuma loja autorizada para a Zippy com este CNPJ.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            return new("UNAVAILABLE", Guid.Empty, "Não foi possível falar com o iFood agora. Tente de novo em instantes.");
        }

        static async Task<(HttpStatusCode, string)> GetAsync(HttpClient client, Uri uri, IfoodAccessToken token, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            using var response = await client.SendAsync(request, ct);
            return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "");
        }
    }

    private IfoodMerchantLookup? Failure(HttpStatusCode status, IfoodAccessToken token)
    {
        switch (status)
        {
            case HttpStatusCode.OK: return null;
            case HttpStatusCode.Unauthorized:
                _tokens.Invalidate(token);
                return new("APP_CREDENTIALS", Guid.Empty, "A Zippy não conseguiu entrar no iFood agora. Avise a Zippy.");
            case HttpStatusCode.TooManyRequests:
                return new("RATE_LIMIT", Guid.Empty, "O iFood pediu para aguardar um pouco. Tente de novo em instantes.");
            default:
                return new("UNAVAILABLE", Guid.Empty, "O iFood não respondeu como esperado. Tente de novo em instantes.");
        }
    }

    /// <summary>IDs da listagem (aceita lista direta ou embrulhada em elements/data/merchants/content/items).</summary>
    internal static IEnumerable<Guid> ListMerchants(string body)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); } catch (JsonException) { yield break; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "elements", "data", "merchants", "content", "items" })
                    if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array) { root = inner; break; }
            if (root.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var merchant)) continue;
                yield return merchant;
            }
        }
    }

    /// <summary>
    /// O iFood pode expor o documento do merchant num campo "documents"/"cnpj"; so um valor com 14 digitos identicos aos
    /// do CNPJ informado, dentro de um desses campos, conta. Nome ou endereco parecidos nunca vinculam uma loja.
    /// </summary>
    internal static bool DocumentMatches(string body, string cnpjDigits)
    {
        if (cnpjDigits.Length != 14) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return Scan(document.RootElement, false, cnpjDigits);
        }
        catch (JsonException) { return false; }

        static bool Scan(JsonElement element, bool inDocument, string cnpj)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var nested = inDocument || property.Name.Contains("cnpj", StringComparison.OrdinalIgnoreCase) ||
                                     property.Name.Contains("document", StringComparison.OrdinalIgnoreCase);
                        if (Scan(property.Value, nested, cnpj)) return true;
                    }
                    return false;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Any(e => Scan(e, inDocument, cnpj));
                case JsonValueKind.String when inDocument:
                    return new string((element.GetString() ?? "").Where(char.IsDigit).ToArray()) == cnpj;
                default:
                    return false;
            }
        }
    }
}
