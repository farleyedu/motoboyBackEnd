using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace APIBack.Integrations.Ifood.Events;

public interface IIfoodEventsClient
{
    Task<IReadOnlyList<IfoodEventEnvelope>> PollAsync(IReadOnlyList<IfoodPollingMerchant> merchants, Func<CancellationToken, Task> beforeSend, CancellationToken ct);
    Task AcknowledgeAsync(IReadOnlyList<IfoodEventReceipt> events, Func<CancellationToken, Task> beforeSend, CancellationToken ct);
}

public sealed class IfoodEventsClient : IIfoodEventsClient
{
    public const string ClientName = "ifood-events";
    private static readonly Uri PollEndpoint = new("https://merchant-api.ifood.com.br/events/v1.0/events:polling");
    private static readonly Uri AckEndpoint = new("https://merchant-api.ifood.com.br/events/v1.0/events/acknowledgment");
    private readonly IHttpClientFactory _http;
    private readonly IfoodMerchantAccess _access;
    private readonly IIfoodTokenProvider _tokens;
    private readonly TimeProvider _clock;
    public IfoodEventsClient(IHttpClientFactory http, IfoodMerchantAccess access, IIfoodTokenProvider tokens, TimeProvider clock)
        => (_http, _access, _tokens, _clock) = (http, access, tokens, clock);

    public async Task<IReadOnlyList<IfoodEventEnvelope>> PollAsync(IReadOnlyList<IfoodPollingMerchant> merchants, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        ValidateMerchants(merchants);
        var token = await TokenAsync(merchants, ct);
        await beforeSend(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, PollEndpoint);
        request.Headers.Add("x-polling-merchants", string.Join(',', merchants.Select(m => m.MerchantId.ToString())));
        using var response = await SendAsync(request, token, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return Array.Empty<IfoodEventEnvelope>();
        if (response.StatusCode != HttpStatusCode.OK) throw Failure(response);
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 10000)
                throw new IfoodIntegrationException("IFOOD_EVENTS_INVALID_RESPONSE");
            return json.RootElement.EnumerateArray().Select(IfoodEventEnvelope.Parse).ToArray();
        }
        catch (JsonException) { throw new IfoodIntegrationException("IFOOD_EVENTS_INVALID_RESPONSE"); }
    }

    public async Task AcknowledgeAsync(IReadOnlyList<IfoodEventReceipt> events, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        if (events.Count is < 1 or > 2000 || events.Any(e => e.EventId == Guid.Empty))
            throw new IfoodIntegrationException("IFOOD_ACK_INVALID_BATCH");
        var merchants = events.GroupBy(e => e.MerchantId).Select(group =>
        {
            if (group.Select(e => e.EstabelecimentoId).Distinct().Count() != 1)
                throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
            return new IfoodPollingMerchant { MerchantId = group.Key, EstabelecimentoId = group.First().EstabelecimentoId };
        }).ToArray();
        ValidateMerchants(merchants);
        var token = await TokenAsync(merchants, ct);
        await beforeSend(ct);
        var body = JsonSerializer.Serialize(events.Select(e => e.EventId).Distinct().Select(id => new { id }));
        using var request = new HttpRequestMessage(HttpMethod.Post, AckEndpoint)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await SendAsync(request, token, ct);
        if (response.StatusCode != HttpStatusCode.Accepted) throw Failure(response);
    }

    private async Task<IfoodAccessToken> TokenAsync(IReadOnlyList<IfoodPollingMerchant> merchants, CancellationToken ct)
    {
        IfoodAccessToken? token = null;
        foreach (var merchant in merchants) token = await _access.GetTokenAsync(merchant.EstabelecimentoId, merchant.MerchantId, ct);
        return token!;
    }
    private static void ValidateMerchants(IReadOnlyList<IfoodPollingMerchant> merchants)
    {
        if (merchants.Count is < 1 or > 100 || merchants.Any(m => m.MerchantId == Guid.Empty || m.EstabelecimentoId == Guid.Empty) ||
            merchants.Select(m => m.MerchantId).Distinct().Count() != merchants.Count)
            throw new IfoodIntegrationException("IFOOD_EVENTS_INVALID_MERCHANTS");
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, IfoodAccessToken token, CancellationToken ct)
    {
        using var client = _http.CreateClient(ClientName);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        try
        {
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) _tokens.Invalidate(token);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { throw new IfoodIntegrationException("IFOOD_EVENTS_TIMEOUT", retryable: true); }
        catch (HttpRequestException) { throw new IfoodIntegrationException("IFOOD_EVENTS_TRANSPORT", retryable: true); }
    }
    private IfoodIntegrationException Failure(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry == null && response.Headers.RetryAfter?.Date is { } date) retry = date - _clock.GetUtcNow();
        if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "IFOOD_EVENTS_UNAUTHORIZED",
            HttpStatusCode.Forbidden => "IFOOD_EVENTS_FORBIDDEN",
            HttpStatusCode.TooManyRequests => "IFOOD_EVENTS_RATE_LIMIT",
            _ => "IFOOD_EVENTS_REMOTE_ERROR"
        };
        return new(code, response.StatusCode, response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500, retry);
    }
}
