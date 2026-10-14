using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Commands;

namespace APIBack.Integrations.Ifood.Exceptions;

public interface IIfoodCancellationReasonsClient
{
    Task<IReadOnlyList<IfoodCancellationReasonDto>> GetAsync(IfoodCommandContext order,Func<CancellationToken,Task> beforeSend,CancellationToken ct);
}
public sealed class IfoodCancellationReasonsClient : IIfoodCancellationReasonsClient
{
    public const string ClientName="ifood-cancellation-reasons";
    private readonly IHttpClientFactory _http;
    private readonly IfoodMerchantAccess _access;
    private readonly IIfoodTokenProvider _tokens;
    public IfoodCancellationReasonsClient(IHttpClientFactory http,IfoodMerchantAccess access,IIfoodTokenProvider tokens)=>(_http,_access,_tokens)=(http,access,tokens);
    public async Task<IReadOnlyList<IfoodCancellationReasonDto>> GetAsync(IfoodCommandContext order,Func<CancellationToken,Task> beforeSend,CancellationToken ct)
    {
        if(order.ExternalOrderId==Guid.Empty) throw new IfoodIntegrationException("IFOOD_EXCEPTION_INVALID_IDENTITY");
        var token=await _access.GetTokenAsync(order.EstabelecimentoId,order.MerchantId,ct);await beforeSend(ct);
        using var client=_http.CreateClient(ClientName);
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://merchant-api.ifood.com.br/order/v1.0/orders/{order.ExternalOrderId:D}/cancellationReasons");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.Value);
        try
        {
            using var response=await client.SendAsync(request,ct);
            if(response.StatusCode!=HttpStatusCode.OK)
            {
                if(response.StatusCode==HttpStatusCode.Unauthorized) _tokens.Invalidate(token);
                var retry=response.Headers.RetryAfter?.Delta;
                if(retry==null && response.Headers.RetryAfter?.Date is {} date) retry=date-DateTimeOffset.UtcNow;
                throw new IfoodIntegrationException("IFOOD_CANCELLATION_REASONS_UNAVAILABLE",response.StatusCode,
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests || (int)response.StatusCode>=500,retry<TimeSpan.Zero?TimeSpan.Zero:retry);
            }
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch(OperationCanceledException) { throw new IfoodIntegrationException("IFOOD_CANCELLATION_REASONS_TIMEOUT",retryable:true); }
        catch(HttpRequestException) { throw new IfoodIntegrationException("IFOOD_CANCELLATION_REASONS_TRANSPORT",retryable:true); }
    }
    public static IReadOnlyList<IfoodCancellationReasonDto> Parse(string json)
    {
        try
        {
            using var document=JsonDocument.Parse(json);var root=document.RootElement;
            if(root.ValueKind==JsonValueKind.Object && root.TryGetProperty("reasons",out var reasons)) root=reasons;
            if(root.ValueKind!=JsonValueKind.Array || root.GetArrayLength()>200) throw Invalid();
            var result=new List<IfoodCancellationReasonDto>();var codes=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in root.EnumerateArray())
            {
                static string? Read(JsonElement item,string field)=>item.ValueKind==JsonValueKind.Object && item.TryGetProperty(field,out var value) && value.ValueKind==JsonValueKind.String?value.GetString():null;
                var code=Read(item,"code");var alias=Read(item,"cancelCodeId");var description=Read(item,"description");
                if(code!=null && alias!=null && code!=alias) throw Invalid();code??=alias;
                if(!IfoodExceptionRules.Text(code,128) || !IfoodExceptionRules.Text(description,1024) || !codes.Add(code!)) throw Invalid();
                result.Add(new(code!,description!));
            }
            return result;
        }
        catch(JsonException) { throw Invalid(); }
    }
    private static IfoodIntegrationException Invalid()=>new("IFOOD_CANCELLATION_REASONS_INVALID");
}
