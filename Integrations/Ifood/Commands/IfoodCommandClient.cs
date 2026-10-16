using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace APIBack.Integrations.Ifood.Commands;

public sealed record IfoodCommandHttpResult(string State,string Code,int? HttpStatus=null,TimeSpan? RetryAfter=null);
public interface IIfoodCommandClient
{
    Task<IfoodCommandHttpResult> SendAsync(IfoodCommandReservation command,Func<CancellationToken,Task> beforeSend,CancellationToken ct);
}
public sealed class IfoodCommandClient : IIfoodCommandClient
{
    public const string ClientName="ifood-order-commands";
    private readonly IHttpClientFactory _http;
    private readonly IfoodMerchantAccess _access;
    private readonly IIfoodTokenProvider _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<IfoodCommandClient>? _logger;
    public IfoodCommandClient(IHttpClientFactory http,IfoodMerchantAccess access,IIfoodTokenProvider tokens,TimeProvider clock,ILogger<IfoodCommandClient>? logger=null)
        => (_http,_access,_tokens,_clock,_logger)=(http,access,tokens,clock,logger);
    public async Task<IfoodCommandHttpResult> SendAsync(IfoodCommandReservation command,Func<CancellationToken,Task> beforeSend,CancellationToken ct)
    {
        if(command.ExternalOrderId==Guid.Empty) throw new IfoodIntegrationException("IFOOD_COMMAND_INVALID_IDENTITY");
        var exception=Exceptions.IfoodExceptionRules.IsKnown(command.Command);
        var intent=exception?Exceptions.IfoodExceptionRules.Intent(command.PayloadJson):null;
        var endpoint=exception?Exceptions.IfoodExceptionRules.Endpoint(command,intent!):new Uri($"https://merchant-api.ifood.com.br/order/v1.0/orders/{command.ExternalOrderId:D}/{IfoodCommandRules.Endpoint(command.Command)}");
        var token=await _access.GetTokenAsync(command.EstabelecimentoId,command.MerchantId,ct);
        using var client=_http.CreateClient(ClientName);
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.Value);
        var payload=exception?intent!.Body.GetRawText():IfoodCommandRules.Payload(command.Command);
        // O iFood recusa com 400 "Invalid mime type" quando o Content-Type leva charset.
        request.Content=new StringContent(payload,Encoding.UTF8,"application/json");
        request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        // Fence e registro de início de envio após token/configuração, imediatamente antes de SendAsync.
        await beforeSend(ct);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            var status=(int)response.StatusCode;
            if(response.StatusCode==HttpStatusCode.Accepted || (Exceptions.IfoodExceptionRules.IsDispute(command.Command) && response.StatusCode==HttpStatusCode.Created))
                return new("accepted","IFOOD_COMMAND_AWAITING_EVENT",status);
            if(Exceptions.IfoodExceptionRules.IsDispute(command.Command) && response.StatusCode==HttpStatusCode.UnprocessableEntity)
                return new("review","IFOOD_DISPUTE_ALREADY_ANSWERED_OR_STATE_UNKNOWN",status);
            if(response.StatusCode==HttpStatusCode.Unauthorized)
            {
                _tokens.Invalidate(token);return new("retry","IFOOD_COMMAND_UNAUTHORIZED",status);
            }
            var delay=response.Headers.RetryAfter?.Delta;
            if(delay==null && response.Headers.RetryAfter?.Date is {} date) delay=date-_clock.GetUtcNow();
            if(response.StatusCode==HttpStatusCode.TooManyRequests) return new("retry","IFOOD_COMMAND_RATE_LIMIT",status,delay<TimeSpan.Zero?TimeSpan.Zero:delay);
            if(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            {
                // A recusa do iFood explica o motivo no corpo. Sem isso o operador ve apenas "recusado".
                await LogRefusalAsync(command,status,payload,response,ct);
                if(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                    return new("review","IFOOD_COMMAND_REMOTE_STATE_CONFLICT",status);
                return new("rejected",response.StatusCode==HttpStatusCode.Forbidden?"IFOOD_COMMAND_FORBIDDEN":"IFOOD_COMMAND_INVALID_REMOTE_REQUEST",status);
            }
            // Inclui 5xx e status inesperados: POST pode ter efeito mesmo sem resposta útil.
            return new("uncertain","IFOOD_COMMAND_REMOTE_RESULT_UNKNOWN",status);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch(OperationCanceledException) { return new("uncertain","IFOOD_COMMAND_TIMEOUT"); }
        catch(HttpRequestException) { return new("uncertain","IFOOD_COMMAND_TRANSPORT"); }
    }
    // Le so um trecho do corpo da recusa e do corpo enviado (codigo de motivo, nunca token ou cabecalho).
    private async Task LogRefusalAsync(IfoodCommandReservation command,int status,string payload,HttpResponseMessage response,CancellationToken ct)
    {
        if(_logger==null) return;
        string detail;
        try
        {
            var body=await response.Content.ReadAsStringAsync(ct);
            detail=(body.Length>500?body[..500]:body).ReplaceLineEndings(" ");
        }
        catch(Exception) { detail="(corpo indisponivel)"; }
        var sent=payload.Length>300?payload[..300]:payload;
        _logger.LogWarning("Comando iFood recusado: {Command} pedido {Order} status {Status} enviado {Sent} resposta {Detail}",
            command.Command,command.ExternalOrderId,status,sent,detail);
    }
}
