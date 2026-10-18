using System.Net;
using System.Net.Http.Headers;

namespace APIBack.Integrations.Ifood.Orders;

public interface IIfoodOrderDetailsClient
{
    Task<string> GetAsync(IfoodDetailsReservation reservation, Func<CancellationToken, Task> beforeSend, CancellationToken ct);
}

public sealed class IfoodOrderDetailsClient : IIfoodOrderDetailsClient
{
    public const string ClientName = "ifood-order-details";
    private readonly IHttpClientFactory _http;
    private readonly IfoodMerchantAccess _access;
    private readonly IIfoodTokenProvider _tokens;
    public IfoodOrderDetailsClient(IHttpClientFactory http, IfoodMerchantAccess access, IIfoodTokenProvider tokens)
        => (_http, _access, _tokens) = (http, access, tokens);
    public async Task<string> GetAsync(IfoodDetailsReservation reservation, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        if (reservation.ExternalOrderId == Guid.Empty) throw new IfoodIntegrationException("IFOOD_ORDER_MISSING_ID");
        var token = await _access.GetTokenAsync(reservation.EstabelecimentoId, reservation.MerchantId, ct);
        await beforeSend(ct);
        using var client = _http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://merchant-api.ifood.com.br/order/v1.0/orders/{reservation.ExternalOrderId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) _tokens.Invalidate(token);
                var retry = response.Headers.RetryAfter?.Delta;
                if (retry == null && response.Headers.RetryAfter?.Date is { } date) retry = date - DateTimeOffset.UtcNow;
                if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "IFOOD_ORDER_UNAUTHORIZED", HttpStatusCode.Forbidden => "IFOOD_ORDER_FORBIDDEN",
                    HttpStatusCode.NotFound => "IFOOD_ORDER_NOT_FOUND", HttpStatusCode.TooManyRequests => "IFOOD_ORDER_RATE_LIMIT",
                    _ => "IFOOD_ORDER_REMOTE_ERROR"
                };
                throw new IfoodIntegrationException(code, response.StatusCode,
                    response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500, retry);
            }
            var sanitized = IfoodOrderNormalizer.Sanitize(await response.Content.ReadAsStringAsync(ct), reservation.MerchantId, reservation.ExternalOrderId);
            var root = System.Text.Json.Nodes.JsonNode.Parse(sanitized)!.AsObject();
            root.Remove("zippyRecovery"); // marcador exclusivamente local, nunca aceito da API remota
            root.Remove(IfoodDeliveryLocation.Key);
            return root.ToJsonString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { throw new IfoodIntegrationException("IFOOD_ORDER_TIMEOUT", retryable: true); }
        catch (HttpRequestException) { throw new IfoodIntegrationException("IFOOD_ORDER_TRANSPORT", retryable: true); }
    }
}
