using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood;

public sealed class IfoodAccessToken
{
    [JsonIgnore] public string Value { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    internal TimeSpan Lifetime { get; }
    internal IfoodAccessToken(string value, DateTimeOffset expiresAtUtc, TimeSpan lifetime)
        => (Value, ExpiresAtUtc, Lifetime) = (value, expiresAtUtc, lifetime);
    public override string ToString() => "[token iFood omitido]";
}

public interface IIfoodAuthenticationClient
{
    Task<IfoodAccessToken> RequestTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class IfoodAuthenticationClient : IIfoodAuthenticationClient
{
    public const string ClientName = "ifood-authentication";
    public static readonly Uri TokenEndpoint = new("https://merchant-api.ifood.com.br/authentication/v1.0/oauth/token");
    private readonly IHttpClientFactory _http;
    private readonly IfoodOptions _options;
    private readonly TimeProvider _clock;
    public IfoodAuthenticationClient(IHttpClientFactory http, IOptions<IfoodOptions> options, TimeProvider clock)
        => (_http, _options, _clock) = (http, options.Value, clock);

    public async Task<IfoodAccessToken> RequestTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new IfoodIntegrationException("IFOOD_DISABLED");
        // Contar desde o envio evita prolongar artificialmente a validade pelo tempo de rede.
        var requestedAt = _clock.GetUtcNow();
        using var client = _http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grantType"] = "client_credentials", ["clientId"] = _options.ClientId,
                ["clientSecret"] = _options.ClientSecret
            })
        };
        try
        {
            // Limita também o conteúdo de respostas de erro; nunca registra seu corpo.
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw Failure(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("accessToken", out var tokenField) || tokenField.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("type", out var typeField) || typeField.ValueKind != JsonValueKind.String ||
                !string.Equals(typeField.GetString(), "bearer", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("expiresIn", out var expiryField) || !expiryField.TryGetInt32(out var seconds) || seconds <= 0)
                throw new IfoodIntegrationException("IFOOD_AUTH_INVALID_RESPONSE");
            var token = tokenField.GetString();
            if (string.IsNullOrEmpty(token) || token.Length > 8000 ||
                !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='))
                throw new IfoodIntegrationException("IFOOD_AUTH_INVALID_RESPONSE");
            var expiry = requestedAt.AddSeconds(seconds);
            if (expiry <= _clock.GetUtcNow()) throw new IfoodIntegrationException("IFOOD_AUTH_EXPIRED_RESPONSE", retryable: true);
            return new IfoodAccessToken(token, expiry, TimeSpan.FromSeconds(seconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException)
        { throw new IfoodIntegrationException("IFOOD_AUTH_TIMEOUT", retryable: true); }
        catch (HttpRequestException)
        { throw new IfoodIntegrationException("IFOOD_AUTH_TRANSPORT", retryable: true); }
        catch (JsonException)
        { throw new IfoodIntegrationException("IFOOD_AUTH_INVALID_RESPONSE"); }
        catch (InvalidOperationException)
        { throw new IfoodIntegrationException("IFOOD_AUTH_INVALID_RESPONSE"); }
    }

    private IfoodIntegrationException Failure(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry == null && response.Headers.RetryAfter?.Date is { } date) retry = date - _clock.GetUtcNow();
        if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
        var code = status switch
        {
            HttpStatusCode.Unauthorized => "IFOOD_AUTH_BAD_CREDENTIALS",
            HttpStatusCode.Forbidden => "IFOOD_AUTH_FORBIDDEN",
            HttpStatusCode.TooManyRequests => "IFOOD_AUTH_RATE_LIMIT",
            _ => "IFOOD_AUTH_REMOTE_ERROR"
        };
        return new IfoodIntegrationException(code, status, status == HttpStatusCode.TooManyRequests || (int)status >= 500, retry);
    }
}
