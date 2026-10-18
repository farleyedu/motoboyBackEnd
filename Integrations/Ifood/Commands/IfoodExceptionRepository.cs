using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Exceptions;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Commands;

public sealed partial class IfoodCommandRepository
{
    public Task<IfoodCommandContext> CancellationContextAsync(Guid store,int pedido,long? expectedRevision=null,CancellationToken ct=default)=>Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await Resolve(c,tx,store,pedido,ct);
        await Authorize(c,tx,store,context.MerchantId,ct);
        IfoodExceptionRules.Enabled(_options,context,"request_cancellation",expectedRevision??context.Revision);IfoodExceptionRules.Cancellation(context);
        await tx.CommitAsync(ct);return context;
    });
    public Task<IfoodCancellationReasonsDto> StoreReasonsAsync(IfoodCommandContext context,IReadOnlyList<IfoodCancellationReasonDto> reasons,CancellationToken ct=default)=>Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockOrder(c,tx,context.MerchantId,context.ExternalOrderId,ct);await Authorize(c,tx,context.EstabelecimentoId,context.MerchantId,ct);
        var current=await Context(c,tx,context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId,ct);
        IfoodExceptionRules.Enabled(_options,current,"request_cancellation",context.Revision);IfoodExceptionRules.Cancellation(current);
        var catalog=Guid.NewGuid();var now=_clock.GetUtcNow();var expires=now.AddSeconds(_options.CancellationReasonsTtlSeconds);
        // Normaliza apenas código/descrição; não grava corpo remoto nem campos desconhecidos.
        var clean=IfoodCancellationReasonsClient.Parse(JsonSerializer.Serialize(new { reasons=reasons.Select(r=>new { code=r.Code,description=r.Description }) }));
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_cancellation_reason_snapshots(catalog_id,merchant_id,external_order_id,estabelecimento_id,expected_order_revision,reasons,fetched_at_utc,expires_at_utc)
VALUES(@Catalog,@MerchantId,@ExternalOrderId,@EstabelecimentoId,@Revision,CAST(@Reasons AS jsonb),@Now,@Expires)
""",new { Catalog=catalog,current.MerchantId,current.ExternalOrderId,current.EstabelecimentoId,current.Revision,Reasons=JsonSerializer.Serialize(clean),Now=now,Expires=expires },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return new IfoodCancellationReasonsDto { CatalogId=catalog,PedidoId=current.PedidoId,Revision=current.Revision,ExpiresAtUtc=expires,Reasons=clean };
    });
    public Task<IfoodDisputeOptionsDto> DisputeOptionsAsync(Guid store,int pedido,Guid dispute,CancellationToken ct=default)=>Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await Resolve(c,tx,store,pedido,ct);await Authorize(c,tx,store,context.MerchantId,ct);
        if(!_options.Enabled || !_options.ExceptionsEnabled || !_options.ExceptionMerchants.Any(m=>m.MerchantId==context.MerchantId && m.EstabelecimentoId==store))
            throw IfoodExceptionRules.Error("IFOOD_EXCEPTIONS_DISABLED");
        var row=await DisputeContext(c,tx,context,dispute,ct);var result=IfoodExceptionRules.Options(row,context.Revision,_clock.GetUtcNow());
        result.Actions=result.Actions.Where(a=>_options.AllowedExceptionActions.Contains(IfoodExceptionRules.Command(a))).ToArray();
        result.CanRespond=result.CanRespond && result.Actions.Count>0;await tx.CommitAsync(ct);return result;
    });
    public Task<IfoodCommandDto> EnqueueCancellationAsync(Guid store,int user,int pedido,IfoodCancellationRequest request,CancellationToken ct=default)
        => EnqueueException(store,user,pedido,request.OperationId,request.ExpectedRevision,"request_cancellation",request.ParentOperationId,
            JsonSerializer.Serialize(new IfoodExceptionIntent { CatalogId=request.CatalogId,Body=JsonSerializer.SerializeToElement(new { reason=request.Reason }) }),ct);
    public Task<IfoodCommandDto> EnqueueDisputeAsync(Guid store,int user,int pedido,Guid dispute,IfoodDisputeResponseRequest request,CancellationToken ct=default)
        => EnqueueException(store,user,pedido,request.OperationId,request.ExpectedRevision,IfoodExceptionRules.Command(request.Action),request.ParentOperationId,
            JsonSerializer.Serialize(new IfoodExceptionIntent { DisputeId=dispute,DisputeRevision=request.ExpectedDisputeRevision,AlternativeId=request.AlternativeId,Response=request,Body=JsonSerializer.SerializeToElement(new { }) }),ct);
    private Task<IfoodCommandDto> EnqueueException(Guid store,int user,int pedido,Guid operation,long revision,string command,Guid? parent,string input,CancellationToken ct)=>Guard(async()=>
    {
        if(operation==Guid.Empty || revision<=0 || user<=0 || store==Guid.Empty) throw new DeliveryDomainException(400,"IFOOD_EXCEPTION_INVALID_REQUEST","Informe operação, ação e revisão válidas.");
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await Resolve(c,tx,store,pedido,ct);await LockOrder(c,tx,context.MerchantId,context.ExternalOrderId,ct);
        await Authorize(c,tx,store,context.MerchantId,ct);
        context=await Context(c,tx,context.MerchantId,context.ExternalOrderId,store,ct);
        var intent=IfoodExceptionRules.Intent(input);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId,user,revision,command,parent,input })))).ToLowerInvariant();
        var previous=await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"SELECT {ReservationColumns} FROM ifood_outbound_commands WHERE operation_id=@Operation FOR UPDATE",new { Operation=operation },tx,cancellationToken:ct));
        if(previous!=null)
        {
            if(previous.EstabelecimentoId!=store || previous.ActorUserId!=user || previous.PayloadHash!=hash) throw IfoodCommandRules.Error("IFOOD_COMMAND_OPERATION_CONFLICT");
            var same=await Read(c,tx,store,operation,ct);await tx.CommitAsync(ct);return same!;
        }
        IfoodExceptionRules.Enabled(_options,context,command,revision);
        DateTimeOffset? deadline=null;string payload;
        if(command=="request_cancellation")
        {
            IfoodExceptionRules.Cancellation(context);await ValidateCatalog(c,tx,context,intent,ct);
            var active=await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId
AND command_type IN('confirm','start_preparation','ready_to_pickup','dispatch','request_cancellation') AND state IN('queued','processing','uncertain','review'))
""",context,tx,cancellationToken:ct));
            if(active) throw IfoodCommandRules.Error("IFOOD_COMMAND_ACTIVE_OPERATION");
            // O iFood exige cancellationCode alem de reason. Mantemos o codigo nos dois campos:
            // e o valor que a API ja aceitava em reason, e o unico que o catalogo garante ser valido.
            payload=JsonSerializer.Serialize(new IfoodExceptionIntent { CatalogId=intent.CatalogId,
                Body=JsonSerializer.SerializeToElement(new { reason=Code(intent),cancellationCode=Code(intent) }) });
        }
        else
        {
            var dispute=await DisputeContext(c,tx,context,intent.DisputeId??Guid.Empty,ct);
            var body=IfoodExceptionRules.Response(dispute,intent.Response??throw IfoodExceptionRules.Error("IFOOD_EXCEPTION_INTENT_INVALID"),_clock.GetUtcNow());
            deadline=dispute.ExpiresAtUtc;
            payload=JsonSerializer.Serialize(new IfoodExceptionIntent { DisputeId=dispute.DisputeId,DisputeRevision=dispute.Revision,AlternativeId=intent.AlternativeId,Response=intent.Response,Body=body });
        }
        if(parent.HasValue)
        {
            var prior=await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"SELECT {ReservationColumns} FROM ifood_outbound_commands WHERE operation_id=@Parent AND merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId FOR UPDATE",
                new { Parent=parent,context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId },tx,cancellationToken:ct));
            if(prior==null || prior.State!="rejected" || (command=="request_cancellation"?prior.Command!=command:IfoodExceptionRules.Intent(prior.PayloadJson).DisputeId!=intent.DisputeId))
                throw IfoodExceptionRules.Error("IFOOD_EXCEPTION_PARENT_NOT_REJECTED");
        }
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_outbound_commands(operation_id,merchant_id,external_order_id,estabelecimento_id,command_type,payload,payload_hash,
expected_order_revision,actor_user_id,parent_operation_id,target_dispute_id,not_before_utc,deadline_at_utc,created_at_utc)
VALUES(@Operation,@MerchantId,@ExternalOrderId,@EstabelecimentoId,@Command,CAST(@Payload AS jsonb),@Hash,@Revision,@User,@Parent,@Dispute,@Now,@Deadline,@Now)
""",new { Operation=operation,context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId,Command=command,Payload=payload,Hash=hash,Revision=revision,User=user,Parent=parent,
            Dispute=intent.DisputeId,Now=_clock.GetUtcNow(),Deadline=deadline },tx,cancellationToken:ct));
        if(intent.DisputeId.HasValue)
            await c.ExecuteAsync(new CommandDefinition("UPDATE ifood_disputes SET response_operation_id=@Operation WHERE merchant_id=@MerchantId AND dispute_id=@Dispute AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId",
                new { Operation=operation,context.MerchantId,Dispute=intent.DisputeId,context.ExternalOrderId,context.EstabelecimentoId },tx,cancellationToken:ct));
        await Notify(c,tx,context,operation,"queued",ct);var result=await Read(c,tx,store,operation,ct);await tx.CommitAsync(ct);return result!;
    });
    public Task<IfoodCommandContext> RenewExceptionLookupAsync(IfoodCommandReservation item,CancellationToken ct=default)=>Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockOrder(c,tx,item.MerchantId,item.ExternalOrderId,ct);var current=await Owned(c,tx,item,ct);
        if(current.SendStartedAtUtc!=null) throw new IfoodIntegrationException("IFOOD_COMMAND_ALREADY_SENT");
        await Authorize(c,tx,item.EstabelecimentoId,item.MerchantId,ct);var context=await Context(c,tx,item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,ct);
        IfoodExceptionRules.Enabled(_options,context,current.Command,current.ExpectedRevision);IfoodExceptionRules.Cancellation(context);
        await c.ExecuteAsync(new CommandDefinition("UPDATE ifood_outbound_commands SET lease_until_utc=clock_timestamp()+make_interval(secs=>@Seconds) WHERE operation_id=@OperationId AND lease_id=@LeaseId",
            new { item.OperationId,item.LeaseId,Seconds=_options.CommandLeaseSeconds },tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return context;
    });
    public Task MarkExceptionSendingAsync(IfoodCommandReservation item,IReadOnlyList<IfoodCancellationReasonDto>? freshReasons=null,DateTimeOffset? fetchedAt=null,CancellationToken ct=default)=>Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockOrder(c,tx,item.MerchantId,item.ExternalOrderId,ct);var current=await Owned(c,tx,item,ct);
        if(current.SendStartedAtUtc!=null) throw new IfoodIntegrationException("IFOOD_COMMAND_ALREADY_SENT");
        if(current.PayloadJson!=item.PayloadJson) throw IfoodExceptionRules.Error("IFOOD_EXCEPTION_INTENT_TAMPERED");
        await Authorize(c,tx,item.EstabelecimentoId,item.MerchantId,ct);var context=await Context(c,tx,item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,ct);
        IfoodExceptionRules.Enabled(_options,context,current.Command,current.ExpectedRevision);var intent=IfoodExceptionRules.Intent(current.PayloadJson);
        if(current.Command=="request_cancellation")
        {
            IfoodExceptionRules.Cancellation(context);var reason=Code(intent);
            if(freshReasons==null || fetchedAt==null || fetchedAt>_clock.GetUtcNow() || fetchedAt.Value.AddSeconds(_options.CancellationReasonsTtlSeconds)<=_clock.GetUtcNow() || !freshReasons.Any(r=>r.Code==reason))
                throw IfoodExceptionRules.Error("IFOOD_CANCELLATION_REASON_NO_LONGER_AVAILABLE");
        }
        else
        {
            var dispute=await DisputeContext(c,tx,context,intent.DisputeId??Guid.Empty,ct);
            var body=IfoodExceptionRules.Response(dispute,intent.Response??throw IfoodExceptionRules.Error("IFOOD_EXCEPTION_INTENT_INVALID"),_clock.GetUtcNow(),current.OperationId);
            if(!JsonNode.DeepEquals(JsonNode.Parse(body.GetRawText()),JsonNode.Parse(intent.Body.GetRawText()))) throw IfoodExceptionRules.Error("IFOOD_EXCEPTION_INTENT_TAMPERED");
        }
        var changed=await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_outbound_commands SET send_started_at_utc=@Now,lease_until_utc=clock_timestamp()+make_interval(secs=>@Seconds),updated_at_utc=NOW()
WHERE operation_id=@OperationId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp() AND send_started_at_utc IS NULL
""",new { item.OperationId,item.LeaseId,Now=_clock.GetUtcNow(),Seconds=_options.CommandLeaseSeconds },tx,cancellationToken:ct));
        if(changed!=1) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        await Journal(c,tx,current,"sending",null,null,ct);
        if(!await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE operation_id=@OperationId AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp())",item,tx,cancellationToken:ct)))
            throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        await tx.CommitAsync(ct);return true;
    });
    private async Task ValidateCatalog(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandContext context,IfoodExceptionIntent intent,CancellationToken ct)
    {
        var code=Code(intent);
        var reasons=await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
SELECT reasons::text FROM ifood_cancellation_reason_snapshots WHERE catalog_id=@Catalog AND merchant_id=@MerchantId AND external_order_id=@ExternalOrderId
AND estabelecimento_id=@EstabelecimentoId AND expected_order_revision=@Revision AND expires_at_utc>@Now
""",new { Catalog=intent.CatalogId,context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId,context.Revision,Now=_clock.GetUtcNow() },tx,cancellationToken:ct));
        if(reasons==null || !(JsonSerializer.Deserialize<IfoodCancellationReasonDto[]>(reasons)??Array.Empty<IfoodCancellationReasonDto>()).Any(r=>r.Code==code))
            throw IfoodExceptionRules.Error("IFOOD_CANCELLATION_REASON_NOT_IN_CURRENT_CATALOG");
    }
    // O codigo fica em cancellationCode. Intencoes gravadas antes desse campo existir trazem o codigo em reason.
    private static string Code(IfoodExceptionIntent intent)
    {
        var field=Orders.IfoodOrderNormalizer.Field(intent.Body,"cancellationCode");
        var code=field.ValueKind==JsonValueKind.String?field.GetString():null;
        if(code==null) return Reason(intent);
        if(!IfoodExceptionRules.Text(code,128)) throw IfoodExceptionRules.Error("IFOOD_CANCELLATION_REASON_INVALID");
        return code;
    }
    private static string Reason(IfoodExceptionIntent intent)
    {
        var field=Orders.IfoodOrderNormalizer.Field(intent.Body,"reason");
        var reason=field.ValueKind==JsonValueKind.String?field.GetString():null;
        if(!IfoodExceptionRules.Text(reason,128)) throw IfoodExceptionRules.Error("IFOOD_CANCELLATION_REASON_INVALID");return reason!;
    }
    private static async Task<IfoodCommandContext> Resolve(NpgsqlConnection c,NpgsqlTransaction tx,Guid store,int pedido,CancellationToken ct)
    {
        var identity=await c.QuerySingleOrDefaultAsync<IfoodCommandContext>(new CommandDefinition("SELECT merchant_id AS MerchantId,external_order_id AS ExternalOrderId FROM ifood_order_links WHERE estabelecimento_id=@Store AND pedido_id=@Pedido",new { Store=store,Pedido=pedido },tx,cancellationToken:ct));
        if(identity==null) throw new DeliveryDomainException(404,"IFOOD_ORDER_NOT_FOUND","Pedido iFood não encontrado.");
        await LockOrder(c,tx,identity.MerchantId,identity.ExternalOrderId,ct);
        return await Context(c,tx,identity.MerchantId,identity.ExternalOrderId,store,ct);
    }
    private static async Task<IfoodDisputeContext> DisputeContext(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandContext context,Guid dispute,CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<IfoodDisputeContext>(new CommandDefinition("""
SELECT dispute_id AS DisputeId,revision AS Revision,state AS State,expires_at_utc AS ExpiresAtUtc,timeout_action AS TimeoutAction,
response_operation_id AS ResponseOperationId,payload::text AS PayloadJson FROM ifood_disputes
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId AND dispute_id=@Dispute FOR UPDATE
""",new { context.MerchantId,context.ExternalOrderId,context.EstabelecimentoId,Dispute=dispute },tx,cancellationToken:ct))
        ?? throw new DeliveryDomainException(404,"IFOOD_DISPUTE_NOT_FOUND","Disputa não encontrada.");
    private static Task<int> ReleaseRejectedDispute(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandReservation current,CancellationToken ct)
        => c.ExecuteAsync(new CommandDefinition("UPDATE ifood_disputes SET response_operation_id=NULL WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId AND response_operation_id=@OperationId AND state='open'",
            current,tx,cancellationToken:ct));
}
