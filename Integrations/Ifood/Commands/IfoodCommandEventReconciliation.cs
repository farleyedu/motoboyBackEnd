using System.Text.Json;
using APIBack.Integrations.Ifood.Events;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Commands;

public static class IfoodCommandEventReconciliation
{
    public static async Task ApplyAsync(NpgsqlConnection c,NpgsqlTransaction tx,IfoodInboxReservation item,string code,string outcome,DateTimeOffset at,CancellationToken ct)
    {
        await Exceptions.IfoodExceptionEventReconciliation.ApplyAsync(c,tx,item,code,outcome,at,ct);
        if(outcome!="applied" || !await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('ifood_command_attempts') IS NOT NULL",transaction:tx,cancellationToken:ct))) return;
        var expected=code switch { "CONFIRMED"=>"confirm","PREPARATION_STARTED"=>"start_preparation","READY_TO_PICKUP"=>"ready_to_pickup","DISPATCHED"=>"dispatch",_=>null };
        if(expected==null && code is not ("CANCELLED" or "CONCLUDED")) return;
        var commands=await c.QueryAsync<IfoodCommandReservation>(new CommandDefinition("""
SELECT operation_id AS OperationId,attempts AS Attempts,command_type AS Command,send_started_at_utc AS SendStartedAtUtc
FROM ifood_outbound_commands WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
AND command_type IN('confirm','start_preparation','ready_to_pickup','dispatch') AND state IN('queued','processing','uncertain','review') FOR UPDATE
""",item,tx,cancellationToken:ct));
        foreach(var command in commands)
        {
            var matching=command.Command==expected && command.SendStartedAtUtc.HasValue && at>=command.SendStartedAtUtc;
            if(!matching && code is not ("CANCELLED" or "CONCLUDED")) continue;
            var state=matching?"succeeded":code=="CANCELLED"?"rejected":"review";
            var reason=matching?"IFOOD_COMMAND_EFFECT_OBSERVED":code=="CANCELLED"?"IFOOD_COMMAND_SUPERSEDED_BY_CANCELLATION":"IFOOD_COMMAND_ORDER_ENDED_WITHOUT_EVIDENCE";
            await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_outbound_commands SET state=@State,lease_id=NULL,lease_until_utc=NULL,finished_at_utc=NOW(),updated_at_utc=NOW(),
last_error_code=CASE WHEN @State='succeeded' THEN NULL ELSE @Reason END,confirmed_event_id=CASE WHEN @State='succeeded' THEN @EventId ELSE NULL END,
result=CAST(@Result AS jsonb) WHERE operation_id=@OperationId
""",new { command.OperationId,State=state,Reason=reason,item.EventId,
                Result=JsonSerializer.Serialize(new { source="event",eventId=item.EventId,code,reason,effectObserved=matching }) },tx,cancellationToken:ct));
            await IfoodCommandRepository.Journal(c,tx,command,state,reason,null,ct);
        }
    }
}
