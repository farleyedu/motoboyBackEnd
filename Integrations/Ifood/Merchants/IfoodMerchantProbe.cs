using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood.Merchants;

/// <param name="Ok">O iFood respondeu que este merchant esta autorizado para o app da Zippy.</param>
/// <param name="Code">Diagnostico curto: AUTHORIZED, NOT_AUTHORIZED, DISABLED, APP_CREDENTIALS, RATE_LIMIT, UNAVAILABLE.</param>
public sealed record IfoodMerchantCheck(bool Ok, string Code, string Message);

public interface IIfoodMerchantProbe
{
    /// <summary>Pergunta ao iFood, com o token do app, se o merchant esta autorizado (nada e gravado).</summary>
    Task<IfoodMerchantCheck> CheckAsync(Guid merchantId, CancellationToken ct);
}

/// <summary>
/// Consulta de verdade se o merchant ja autorizou o app da Zippy: o iFood so responde os dados do merchant quando a
/// autorizacao existe; sem ela recusa (403/404).
/// </summary>
public sealed class IfoodMerchantProbe : IIfoodMerchantProbe
{
    public const string ClientName = "ifood-merchant-probe";
    private readonly IHttpClientFactory _http;
    private readonly IIfoodTokenProvider _tokens;
    private readonly IOptionsMonitor<IfoodOptions> _options;

    public IfoodMerchantProbe(IHttpClientFactory http, IIfoodTokenProvider tokens, IOptionsMonitor<IfoodOptions> options)
        => (_http, _tokens, _options) = (http, tokens, options);

    public async Task<IfoodMerchantCheck> CheckAsync(Guid merchantId, CancellationToken ct)
    {
        if (merchantId == Guid.Empty) return new(false, "NOT_AUTHORIZED", "Informe o ID da loja no iFood.");
        if (!_options.CurrentValue.Enabled) return new(false, "DISABLED", "A integração com o iFood está desligada no momento.");

        IfoodAccessToken token;
        try { token = await _tokens.GetAsync(ct); }
        catch (IfoodIntegrationException) { return new(false, "APP_CREDENTIALS", "A Zippy não conseguiu entrar no iFood agora. Avise a Zippy."); }

        using var client = _http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://merchant-api.ifood.com.br/merchant/v1.0/merchants/{merchantId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        try
        {
            using var response = await client.SendAsync(request, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return new(true, "AUTHORIZED", "O iFood confirmou a conexão desta loja.");
                case HttpStatusCode.Unauthorized:
                    _tokens.Invalidate(token);
                    return new(false, "APP_CREDENTIALS", "A Zippy não conseguiu entrar no iFood agora. Avise a Zippy.");
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotFound:
                    return new(false, "NOT_AUTHORIZED", "O iFood ainda não autorizou esta loja para a Zippy.");
                case HttpStatusCode.TooManyRequests:
                    return new(false, "RATE_LIMIT", "O iFood pediu para aguardar um pouco. Tente de novo em instantes.");
                default:
                    return new(false, "UNAVAILABLE", "O iFood não respondeu como esperado. Tente de novo em instantes.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            return new(false, "UNAVAILABLE", "Não foi possível falar com o iFood agora. Tente de novo em instantes.");
        }
    }
}
