using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace APIBack.Integrations.Ifood.Completion;

public sealed record IfoodDeliveryCodeResult(string State, string Code, int? HttpStatus = null);
public interface IIfoodDeliveryCodeClient
{
    Task<IfoodDeliveryCodeResult> SendAsync(Guid store, Guid merchant, Guid order, string code,
        Func<CancellationToken, Task> beforeSend, CancellationToken ct);
}

public sealed class IfoodDeliveryCodeClient(IHttpClientFactory http, IfoodMerchantAccess access, IIfoodTokenProvider tokens) : IIfoodDeliveryCodeClient
{
    public const string ClientName = "ifood-delivery-code";
    public async Task<IfoodDeliveryCodeResult> SendAsync(Guid store, Guid merchant, Guid order, string code,
        Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        var token = await access.GetTokenAsync(store, merchant, ct);
        using var client = http.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://merchant-api.ifood.com.br/order/v1.0/orders/{order:D}/verifyDeliveryCode");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
        // Intenção e marcador de envio confirmados no banco antes do POST. Nunca repetir envio incerto.
        await beforeSend(ct);
        try
        {
            using var response = await client.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized) tokens.Invalidate(token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("valid", out var valid))
                {
                    if (valid.ValueKind == JsonValueKind.True) return new("verified", "IFOOD_COMPLETION_CODE_CONFIRMED", status);
                    if (valid.ValueKind == JsonValueKind.False) return new("rejected", "IFOOD_DELIVERY_CODE_INVALID", status);
                }
            }
            // 422 pode representar código já utilizado/estado incompatível: não presumir erro corrigível.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new("review", "IFOOD_COMPLETION_REMOTE_STATE_REVIEW", status);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.PreconditionFailed)
                return new("rejected", "IFOOD_COMPLETION_REMOTE_REJECTED", status);
            return new("uncertain", "IFOOD_COMPLETION_REMOTE_RESULT_UNKNOWN", status);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        { return new("uncertain", "IFOOD_COMPLETION_REMOTE_RESULT_UNKNOWN"); }
    }
}
