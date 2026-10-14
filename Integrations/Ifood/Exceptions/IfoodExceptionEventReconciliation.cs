using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Orders;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Exceptions;

public static class IfoodExceptionEventReconciliation
{
    public static async Task ApplyAsync(NpgsqlConnection c,NpgsqlTransaction tx,IfoodInboxReservation item,string code,string outcome,DateTimeOffset at,CancellationToken ct)
    {
        if(code is not ("CANCELLATION_REQUESTED" or "CANCELLATION_REQUEST_FAILED" or "CANCELLED" or "CONCLUDED" or "HANDSHAKE_SETTLEMENT") ||
            outcome is not ("applied" or "review") || !await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('ifood_cancellation_reason_snapshots') IS NOT NULL",transaction:tx,cancellationToken:ct))) return;
        using var document=JsonDocument.Parse(item.PayloadJson);var meta=IfoodOrderNormalizer.Field(document.RootElement,"metadata");
        var operations=await c.QueryAsync<IfoodCommandReservation>(new CommandDefinition("""
SELECT operation_id AS OperationId,command_type AS Command,attempts AS Attempts,send_started_at_utc AS SendStartedAtUtc,payload::text AS PayloadJson
FROM ifood_outbound_commands WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
AND command_type IN('request_cancellation','dispute_accept','dispute_reject','dispute_alternative') AND state IN('queued','processing','uncertain','review') FOR UPDATE
""",item,tx,cancellationToken:ct));
        foreach(var operation in operations)
        {
            var sent=operation.SendStartedAtUtc.HasValue && at>=operation.SendStartedAtUtc;
            if(operation.Command=="request_cancellation")
            {
                if(outcome!="applied") continue;
                if(code=="CANCELLATION_REQUESTED" && sent)
                {
                    await c.ExecuteAsync(new CommandDefinition("UPDATE ifood_outbound_commands SET cancellation_requested_event_id=@EventId WHERE operation_id=@OperationId AND cancellation_requested_event_id IS NULL",
                        new { operation.OperationId,item.EventId },tx,cancellationToken:ct));continue;
                }
                if(code=="CANCELLATION_REQUEST_FAILED" && sent)
                {
                    var requested=await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("SELECT cancellation_requested_event_id FROM ifood_outbound_commands WHERE operation_id=@OperationId",operation,tx,cancellationToken:ct));
                    var reference=IfoodOrderNormalizer.Text(meta,"CANCELLATION_REQUESTED_EVENT_ID");
                    var linked=requested.HasValue && Guid.TryParse(reference,out var id) && id==requested;
                    await Resolve(c,tx,item,operation,linked?"rejected":"review",linked?"IFOOD_CANCELLATION_REQUEST_FAILED":"IFOOD_CANCELLATION_REJECTION_UNCORRELATED",ct);continue;
                }
                if(code=="CANCELLED") await Resolve(c,tx,item,operation,sent?"succeeded":"rejected",sent?"IFOOD_CANCELLATION_EFFECT_OBSERVED":"IFOOD_COMMAND_SUPERSEDED_BY_CANCELLATION",ct);
                else if(code=="CONCLUDED") await Resolve(c,tx,item,operation,"review","IFOOD_CANCELLATION_ORDER_ENDED_WITHOUT_EVIDENCE",ct);
                continue;
            }
            if(code!="HANDSHAKE_SETTLEMENT" || !sent) continue;
            var intent=IfoodExceptionRules.Intent(operation.PayloadJson);
            if(!Guid.TryParse(IfoodOrderNormalizer.Text(meta,"disputeId"),out var dispute) || dispute!=intent.DisputeId) continue;
            var status=IfoodOrderNormalizer.Text(meta,"status");
            var state="review";var reason="IFOOD_DISPUTE_SETTLEMENT_REQUIRES_REVIEW";
            if(outcome=="applied")
            {
                if(status=="EXPIRED") { state="rejected";reason="IFOOD_DISPUTE_EXPIRED_REMOTELY"; }
                else if(operation.Command=="dispute_accept" && status=="ACCEPTED" || operation.Command=="dispute_reject" && status=="REJECTED")
                { state="succeeded";reason="IFOOD_DISPUTE_RESPONSE_EFFECT_OBSERVED"; }
                else if(operation.Command=="dispute_alternative" && status=="ALTERNATIVE_REPLIED")
                {
                    if(AlternativeMatches(meta,intent)) { state="succeeded";reason="IFOOD_DISPUTE_ALTERNATIVE_EFFECT_OBSERVED"; }
                }
                else if(status is "ACCEPTED" or "REJECTED") { state="rejected";reason="IFOOD_DISPUTE_RESOLVED_WITH_DIFFERENT_RESPONSE"; }
            }
            await Resolve(c,tx,item,operation,state,reason,ct);
        }
    }
    private static bool AlternativeMatches(JsonElement meta,IfoodExceptionIntent intent)
    {
        var selected=IfoodOrderNormalizer.Field(meta,"selectedDisputeAlternative");
        if(!Guid.TryParse(IfoodOrderNormalizer.Text(selected,"id"),out var id) || id!=intent.AlternativeId ||
            IfoodOrderNormalizer.Text(selected,"type")!=IfoodOrderNormalizer.Text(intent.Body,"type")) return false;
        var actual=IfoodOrderNormalizer.Field(selected,"metadata");var expected=IfoodOrderNormalizer.Field(intent.Body,"metadata");
        var type=IfoodOrderNormalizer.Text(selected,"type");
        if(type is "REFUND" or "BENEFIT")
        {
            var amount=IfoodOrderNormalizer.Field(actual,"amount");var expectedAmount=IfoodOrderNormalizer.Field(expected,"amount");
            var raw=IfoodOrderNormalizer.Text(amount,"value");var wanted=IfoodOrderNormalizer.Text(expectedAmount,"value");
            return raw is { Length:>0 and <=18 } && wanted is { Length:>0 and <=18 } && raw.All(char.IsAsciiDigit) && wanted.All(char.IsAsciiDigit) &&
                long.TryParse(raw,out var a) && long.TryParse(wanted,out var b) && a==b && IfoodOrderNormalizer.Text(amount,"currency")==IfoodOrderNormalizer.Text(expectedAmount,"currency");
        }
        var reason=IfoodOrderNormalizer.Text(actual,"additionalTimeReason")??IfoodOrderNormalizer.Text(actual,"reason");
        var alternateReason=IfoodOrderNormalizer.Text(actual,"reason");
        if(alternateReason!=null && IfoodOrderNormalizer.Text(actual,"additionalTimeReason") is {} primary && primary!=alternateReason) return false;
        var actualMinutes=IfoodOrderNormalizer.Field(actual,"additionalTimeInMinutes");var expectedMinutes=IfoodOrderNormalizer.Field(expected,"additionalTimeInMinutes");
        return reason==IfoodOrderNormalizer.Text(expected,"additionalTimeReason") && actualMinutes.ValueKind==JsonValueKind.Number &&
            expectedMinutes.ValueKind==JsonValueKind.Number && actualMinutes.TryGetInt32(out var minutes) && expectedMinutes.TryGetInt32(out var wantedMinutes) && minutes==wantedMinutes;
    }
    private static async Task Resolve(NpgsqlConnection c,NpgsqlTransaction tx,IfoodInboxReservation item,IfoodCommandReservation operation,string state,string reason,CancellationToken ct)
    {
        await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_outbound_commands SET state=@State,lease_id=NULL,lease_until_utc=NULL,finished_at_utc=NOW(),updated_at_utc=NOW(),
last_error_code=CASE WHEN @State='succeeded' THEN NULL ELSE @Reason END,confirmed_event_id=CASE WHEN @State='succeeded' THEN @EventId ELSE NULL END,
result=CAST(@Result AS jsonb) WHERE operation_id=@OperationId
""",new { operation.OperationId,State=state,Reason=reason,item.EventId,
            Result=JsonSerializer.Serialize(new { source="event",eventId=item.EventId,reason,effectObserved=state=="succeeded" }) },tx,cancellationToken:ct));
        await IfoodCommandRepository.Journal(c,tx,operation,state,reason,null,ct);
    }
}
