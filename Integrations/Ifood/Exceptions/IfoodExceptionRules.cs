using System.Globalization;
using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;

namespace APIBack.Integrations.Ifood.Exceptions;

public sealed class IfoodDisputeContext
{
    public Guid DisputeId { get; set; } public long Revision { get; set; }
    public string State { get; set; } = ""; public DateTimeOffset ExpiresAtUtc { get; set; }
    public string TimeoutAction { get; set; } = ""; public Guid? ResponseOperationId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string PayloadJson { get; set; } = "{}";
    public override string ToString()=>"[disputa iFood; payload omitido]";
}
public sealed class IfoodExceptionIntent
{
    public Guid? DisputeId { get; init; }
    public long? DisputeRevision { get; init; }
    public Guid? AlternativeId { get; init; }
    public Guid? CatalogId { get; init; }
    public IfoodDisputeResponseRequest? Response { get; init; }
    public JsonElement Body { get; init; }
}
public static class IfoodExceptionRules
{
    public static readonly string[] All = { "request_cancellation", "dispute_accept", "dispute_reject", "dispute_alternative" };
    public static bool IsKnown(string command)=>command is "request_cancellation" or "dispute_accept" or "dispute_reject" or "dispute_alternative";
    public static bool IsDispute(string command)=>command is "dispute_accept" or "dispute_reject" or "dispute_alternative";
    public static void Enabled(IfoodOptions options,IfoodCommandContext order,string command,long revision)
    {
        if(!options.Enabled || !options.ExceptionsEnabled) throw Error("IFOOD_EXCEPTIONS_DISABLED");
        if(!IsKnown(command) || !options.AllowedExceptionActions.Contains(command)) throw Error("IFOOD_EXCEPTION_ACTION_NOT_ENABLED");
        if(!options.ExceptionMerchants.Any(m=>m.MerchantId==order.MerchantId && m.EstabelecimentoId==order.EstabelecimentoId)) throw Error("IFOOD_EXCEPTION_MERCHANT_NOT_ENABLED");
        if(order.Revision!=revision) throw Error("IFOOD_COMMAND_STALE_REVISION");
        if(IfoodOrderNormalizer.Normalize(order.Payload,order.MerchantId,order.ExternalOrderId).Category!="FOOD") throw Error("IFOOD_EXCEPTION_CATEGORY_NOT_SUPPORTED");
    }
    public static void Cancellation(IfoodCommandContext order)
    {
        if(order.RemoteStatus is "CANCELLED" or "CONCLUDED") throw Error("IFOOD_ORDER_TERMINAL");
        if(order.RemoteStatus is not ("PLACED" or "CONFIRMED" or "DISPATCHED")) throw Error("IFOOD_EXCEPTION_REMOTE_STATE_UNKNOWN");
        if(order.CancellationState=="requested") throw Error("IFOOD_CANCELLATION_PENDING");
    }
    public static IfoodDisputeOptionsDto Options(IfoodDisputeContext dispute,long orderRevision,DateTimeOffset now)
    {
        var result=new IfoodDisputeOptionsDto { DisputeId=dispute.DisputeId,Revision=dispute.Revision,OrderRevision=orderRevision,State=dispute.State,
            ExpiresAtUtc=dispute.ExpiresAtUtc,TimeoutAction=dispute.TimeoutAction,ResponseOperationId=dispute.ResponseOperationId };
        if(dispute.State!="open" || now>=dispute.ExpiresAtUtc) return result;
        using var document=JsonDocument.Parse(dispute.PayloadJson);var meta=document.RootElement;
        var action=IfoodOrderNormalizer.Text(meta,"action");var type=IfoodOrderNormalizer.Text(meta,"handshakeType");
        if(!((action=="CANCELLATION" && type is "AFTER_DELIVERY" or "PREPARATION_TIME" or "DELAY") ||
            (action=="PARTIAL_CANCELLATION" && type=="AFTER_DELIVERY_PARTIALLY"))) return result;
        var reasons=Strings(Field(meta,"acceptCancellationReasons"));
        var actions=new List<string> { "accept","reject" };
        if(type=="DELAY" && reasons.Count==0) actions.Remove("accept");
        var alternatives=new List<IfoodDisputeAlternativeDto>();var rawAlternatives=IfoodOrderNormalizer.Field(meta,"alternatives");
        var seen=new HashSet<Guid>();
        if(rawAlternatives.ValueKind==JsonValueKind.Array)
            foreach(var option in rawAlternatives.EnumerateArray())
            {
                if(!Guid.TryParse(IfoodOrderNormalizer.Text(option,"id"),out var id) || id==Guid.Empty || !seen.Add(id)) throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
                var alternativeType=IfoodOrderNormalizer.Text(option,"type");var data=IfoodOrderNormalizer.Field(option,"metadata");
                if(alternativeType is "REFUND" or "BENEFIT")
                {
                    var amount=IfoodOrderNormalizer.Field(data,"maxAmount");var value=IfoodOrderNormalizer.Text(amount,"value");var currency=IfoodOrderNormalizer.Text(amount,"currency");
                    if(currency=="BRL" && TryMinor(value,out var max) && max>0)
                        alternatives.Add(new(id,alternativeType,value,currency,Array.Empty<int>(),Array.Empty<string>()));
                }
                else if(alternativeType=="ADDITIONAL_TIME" && type=="DELAY")
                {
                    var minutes=IfoodOrderNormalizer.Field(data,"allowedsAdditionalTimeInMinutes");var timeReasons=Strings(IfoodOrderNormalizer.Field(data,"allowedsAdditionalTimeReasons"));
                    if(minutes.ValueKind==JsonValueKind.Array)
                    {
                        var values=new List<int>();
                        foreach(var minute in minutes.EnumerateArray())
                            if(minute.ValueKind==JsonValueKind.Number && minute.TryGetInt32(out var m) && m>0) values.Add(m);
                            else throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
                        if(values.Count>0 && timeReasons.Count>0) alternatives.Add(new(id,alternativeType,null,null,values.Distinct().ToArray(),timeReasons));
                    }
                }
            }
        else if(rawAlternatives.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
        if(alternatives.Count>0) actions.Add("alternative");
        var restrictions=Field(meta,"allowedActions");
        if(restrictions.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var allowed=Strings(restrictions).Select(s=>s.ToLowerInvariant()).ToArray();actions=actions.Where(allowed.Contains).ToList();
        }
        result.Actions=actions;result.AcceptReasons=reasons;result.Alternatives=alternatives;
        result.CanRespond=actions.Count>0 && dispute.ResponseOperationId==null;return result;
    }
    public static JsonElement Response(IfoodDisputeContext dispute,IfoodDisputeResponseRequest request,DateTimeOffset now,Guid? ownOperation=null)
    {
        if(dispute.Revision!=request.ExpectedDisputeRevision) throw Error("IFOOD_DISPUTE_STALE_REVISION");
        if(dispute.State!="open" || now>=dispute.ExpiresAtUtc) throw Error("IFOOD_DISPUTE_CLOSED_OR_EXPIRED");
        if(dispute.ResponseOperationId.HasValue && dispute.ResponseOperationId!=ownOperation) throw Error("IFOOD_DISPUTE_RESPONSE_ALREADY_PENDING");
        var options=Options(dispute,request.ExpectedRevision,now);
        if(!options.Actions.Contains(request.Action)) throw Error("IFOOD_DISPUTE_ACTION_NOT_AVAILABLE");
        if(request.Action=="accept")
        {
            if(request.AlternativeId!=null || request.AmountMinor!=null || request.Currency!=null || request.AdditionalTimeInMinutes!=null) throw Error("IFOOD_DISPUTE_RESPONSE_INVALID");
            if(options.AcceptReasons.Count>0)
            {
                if(request.Reason==null || !options.AcceptReasons.Contains(request.Reason)) throw Error("IFOOD_DISPUTE_REASON_NOT_ALLOWED");
                return JsonSerializer.SerializeToElement(new { reason=request.Reason });
            }
            if(request.Reason!=null) throw Error("IFOOD_DISPUTE_RESPONSE_INVALID");
            return JsonSerializer.SerializeToElement(new { });
        }
        if(request.Action=="reject")
        {
            if(!Text(request.Reason,500) || request.AlternativeId!=null || request.AmountMinor!=null || request.Currency!=null || request.AdditionalTimeInMinutes!=null)
                throw Error("IFOOD_DISPUTE_RESPONSE_INVALID");
            return JsonSerializer.SerializeToElement(new { reason=request.Reason });
        }
        var alternative=options.Alternatives.SingleOrDefault(a=>a.Id==request.AlternativeId) ?? throw Error("IFOOD_DISPUTE_ALTERNATIVE_NOT_AVAILABLE");
        if(alternative.Type is "REFUND" or "BENEFIT")
        {
            if(!TryMinor(request.AmountMinor,out var selected) || selected<=0 || !TryMinor(alternative.MaxAmountMinor,out var max) || selected>max ||
                request.Currency!=alternative.Currency || request.Reason!=null || request.AdditionalTimeInMinutes!=null) throw Error("IFOOD_DISPUTE_AMOUNT_NOT_ALLOWED");
            return JsonSerializer.SerializeToElement(new { type=alternative.Type,metadata=new { amount=new { value=request.AmountMinor,currency=request.Currency } } });
        }
        if(!alternative.AllowedMinutes.Contains(request.AdditionalTimeInMinutes??0) || request.Reason==null || !alternative.AllowedReasons.Contains(request.Reason) ||
            request.AmountMinor!=null || request.Currency!=null) throw Error("IFOOD_DISPUTE_TIME_NOT_ALLOWED");
        return JsonSerializer.SerializeToElement(new { type=alternative.Type,metadata=new { additionalTimeInMinutes=request.AdditionalTimeInMinutes,additionalTimeReason=request.Reason } });
    }
    public static string Command(string action)=>action switch { "accept"=>"dispute_accept","reject"=>"dispute_reject","alternative"=>"dispute_alternative",_=>throw Error("IFOOD_DISPUTE_ACTION_NOT_AVAILABLE") };
    public static Uri Endpoint(IfoodCommandReservation item,IfoodExceptionIntent intent)
    {
        if(item.ExternalOrderId==Guid.Empty) throw Error("IFOOD_EXCEPTION_INVALID_IDENTITY");
        if(item.Command=="request_cancellation") return new($"https://merchant-api.ifood.com.br/order/v1.0/orders/{item.ExternalOrderId:D}/requestCancellation");
        if(intent.DisputeId is not {} dispute || dispute==Guid.Empty) throw Error("IFOOD_EXCEPTION_INVALID_IDENTITY");
        var suffix=item.Command switch { "dispute_accept"=>"accept","dispute_reject"=>"reject","dispute_alternative" when intent.AlternativeId is {} id && id!=Guid.Empty=>$"alternatives/{id:D}",_=>throw Error("IFOOD_EXCEPTION_INVALID_IDENTITY") };
        return new($"https://merchant-api.ifood.com.br/order/v1.0/disputes/{dispute:D}/{suffix}");
    }
    public static IfoodExceptionIntent Intent(string json)
    {
        try
        {
            var intent=JsonSerializer.Deserialize<IfoodExceptionIntent>(json) ?? throw Error("IFOOD_EXCEPTION_INTENT_INVALID");
            if(intent.Body.ValueKind!=JsonValueKind.Object) throw Error("IFOOD_EXCEPTION_INTENT_INVALID");
            return intent;
        }
        catch(JsonException) { throw Error("IFOOD_EXCEPTION_INTENT_INVALID"); }
    }
    public static bool Text(string? text,int max)=>!string.IsNullOrWhiteSpace(text) && text.Length<=max && !text.Any(char.IsControl) && text==text.Trim();
    private static bool TryMinor(string? value,out long amount)
    {
        amount=0;return value is { Length:>0 and <=18 } && value.All(char.IsAsciiDigit) &&
            long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out amount);
    }
    private static IReadOnlyList<string> Strings(JsonElement array)
    {
        if(array.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Array.Empty<string>();
        if(array.ValueKind!=JsonValueKind.Array) throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
        var values=new List<string>();
        foreach(var field in array.EnumerateArray())
            if(field.ValueKind==JsonValueKind.String && Text(field.GetString(),128)) values.Add(field.GetString()!);
            else throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
        return values.Distinct().ToArray();
    }
    private static JsonElement Field(JsonElement meta,string name)
    {
        var root=IfoodOrderNormalizer.Field(meta,name);var nested=IfoodOrderNormalizer.Field(IfoodOrderNormalizer.Field(meta,"metadata"),name);
        if(root.ValueKind!=JsonValueKind.Undefined && nested.ValueKind!=JsonValueKind.Undefined && root.GetRawText()!=nested.GetRawText()) throw Error("IFOOD_DISPUTE_OPTIONS_INVALID");
        return root.ValueKind==JsonValueKind.Undefined?nested:root;
    }
    public static DeliveryDomainException Error(string code)=>new(409,code,"A solicitação mudou, venceu ou esta opção não está disponível. Atualize o pedido e a disputa.");
}
