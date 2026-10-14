using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Integrations.Ifood.Commands;

public sealed class IfoodCommandReservation
{
    public Guid OperationId { get; set; } public Guid MerchantId { get; set; } public Guid ExternalOrderId { get; set; }
    public Guid EstabelecimentoId { get; set; } public Guid? LeaseId { get; set; }
    public string Command { get; set; } = ""; public string State { get; set; } = ""; public int Attempts { get; set; }
    public int? ActorUserId { get; set; } public long ExpectedRevision { get; set; }
    public DateTimeOffset? SendStartedAtUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string PayloadHash { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string PayloadJson { get; set; } = "{}";
    public override string ToString() => "[reserva de comando iFood; dados omitidos]";
}
public sealed partial class IfoodCommandRepository
{
    private readonly NpgsqlDataSource _source;
    private readonly IfoodOptions _options;
    private readonly TimeProvider _clock;
    public IfoodCommandRepository(NpgsqlDataSource source,IOptions<IfoodOptions> options,TimeProvider clock)
        => (_source,_options,_clock)=(source,options.Value,clock);
    private const string ReservationColumns="""
operation_id AS OperationId,merchant_id AS MerchantId,external_order_id AS ExternalOrderId,estabelecimento_id AS EstabelecimentoId,
lease_id AS LeaseId,command_type AS Command,state AS State,attempts AS Attempts,actor_user_id AS ActorUserId,
expected_order_revision AS ExpectedRevision,send_started_at_utc AS SendStartedAtUtc,payload_hash AS PayloadHash,payload::text AS PayloadJson
""";

    public Task<IfoodCommandDto> EnqueueAsync(Guid store,int user,int pedido,IfoodCommandRequest request,CancellationToken ct=default) => Guard(async()=>
    {
        if(request.OperationId==Guid.Empty || request.ExpectedRevision<=0 || user<=0 || store==Guid.Empty || !IfoodCommandRules.IsKnown(request.Command))
            throw new DeliveryDomainException(400,"IFOOD_COMMAND_INVALID_REQUEST","Informe operação, ação e revisão válidas.");
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var identity=await c.QuerySingleOrDefaultAsync<IfoodCommandContext>(new CommandDefinition("""
SELECT merchant_id AS MerchantId,external_order_id AS ExternalOrderId,estabelecimento_id AS EstabelecimentoId,pedido_id AS PedidoId
FROM ifood_order_links WHERE estabelecimento_id=@Store AND pedido_id=@Pedido
""",new { Store=store,Pedido=pedido },tx,cancellationToken:ct));
        if(identity==null) throw new DeliveryDomainException(404,"IFOOD_ORDER_NOT_FOUND","Pedido iFood não encontrado.");
        await LockOrder(c,tx,identity.MerchantId,identity.ExternalOrderId,ct);
        var hash=Hash(identity,user,request);
        var previous=await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"SELECT {ReservationColumns} FROM ifood_outbound_commands WHERE operation_id=@OperationId FOR UPDATE",request,tx,cancellationToken:ct));
        if(previous!=null)
        {
            if(previous.EstabelecimentoId!=store || previous.PayloadHash!=hash || previous.ActorUserId!=user) throw IfoodCommandRules.Error("IFOOD_COMMAND_OPERATION_CONFLICT");
            await Authorize(c,tx,store,identity.MerchantId,ct);
            var duplicate=await Read(c,tx,store,request.OperationId,ct);await tx.CommitAsync(ct);return duplicate!;
        }
        await Authorize(c,tx,store,identity.MerchantId,ct);
        var context=await Context(c,tx,identity.MerchantId,identity.ExternalOrderId,store,ct);
        var window=IfoodCommandRules.Validate(request.Command,context,request.ExpectedRevision,_clock.GetUtcNow(),_options);
        var active=await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId
AND command_type IN('confirm','start_preparation','ready_to_pickup','dispatch','request_cancellation') AND state IN('queued','processing','uncertain','review'))
""",identity,tx,cancellationToken:ct));
        if(active) throw IfoodCommandRules.Error("IFOOD_COMMAND_ACTIVE_OPERATION");
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_outbound_commands(operation_id,merchant_id,external_order_id,estabelecimento_id,command_type,payload,payload_hash,
expected_order_revision,actor_user_id,not_before_utc,deadline_at_utc,created_at_utc)
VALUES(@OperationId,@MerchantId,@ExternalOrderId,@EstabelecimentoId,@Command,CAST(@Payload AS jsonb),@Hash,@ExpectedRevision,@User,@NotBeforeUtc,@DeadlineAtUtc,@Now)
""",new { request.OperationId,identity.MerchantId,identity.ExternalOrderId,identity.EstabelecimentoId,request.Command,
            Payload=IfoodCommandRules.Payload(request.Command),Hash=hash,request.ExpectedRevision,User=user,window.NotBeforeUtc,window.DeadlineAtUtc,Now=_clock.GetUtcNow() },tx,cancellationToken:ct));
        await Notify(c,tx,context,request.OperationId,"queued",ct);
        var result=await Read(c,tx,store,request.OperationId,ct);await tx.CommitAsync(ct);return result!;
    });
    public Task<IfoodCommandDto?> ReadAsync(Guid store,Guid operation,CancellationToken ct=default) => Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);return await Read(c,null,store,operation,ct);
    });
    private static Task<IfoodCommandDto?> Read(NpgsqlConnection c,NpgsqlTransaction? tx,Guid store,Guid operation,CancellationToken ct)
        => c.QuerySingleOrDefaultAsync<IfoodCommandDto>(new CommandDefinition("""
SELECT o.operation_id AS OperationId,l.pedido_id AS PedidoId,o.command_type AS Command,o.state AS State,
o.expected_order_revision AS ExpectedRevision,o.attempts AS Attempts,o.created_at_utc AS CreatedAtUtc,o.not_before_utc AS NotBeforeUtc,
o.deadline_at_utc AS DeadlineAtUtc,o.send_started_at_utc AS SendStartedAtUtc,o.http_accepted_at_utc AS HttpAcceptedAtUtc,
o.finished_at_utc AS FinishedAtUtc,o.confirmed_event_id AS ConfirmedEventId,o.last_error_code AS LastErrorCode
FROM ifood_outbound_commands o JOIN ifood_order_links l USING(merchant_id,external_order_id,estabelecimento_id)
WHERE o.estabelecimento_id=@Store AND o.operation_id=@Operation
""",new { Store=store,Operation=operation },tx,cancellationToken:ct));

    public Task<IfoodCommandReservation?> ClaimAsync(CancellationToken ct=default,string[]? commands=null,Guid[]? merchants=null) => Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
WITH expired AS (
UPDATE ifood_outbound_commands SET state=CASE WHEN send_started_at_utc IS NOT NULL THEN 'uncertain' WHEN attempts>=@Max THEN 'review' ELSE 'queued' END,
lease_id=NULL,lease_until_utc=NULL,last_error_code=CASE WHEN send_started_at_utc IS NOT NULL THEN 'IFOOD_COMMAND_SEND_RESULT_UNKNOWN' ELSE 'IFOOD_COMMAND_LEASE_EXPIRED' END,
updated_at_utc=NOW() WHERE state='processing' AND lease_until_utc<=clock_timestamp()
AND command_type=ANY(@RecoveryCommands)
RETURNING operation_id,attempts,state,last_error_code)
INSERT INTO ifood_command_attempts(operation_id,attempt_number,stage,code)
SELECT operation_id,attempts,CASE WHEN state='queued' THEN 'retry' ELSE state END,last_error_code FROM expired ON CONFLICT DO NOTHING
""",new { Max=_options.CommandMaxAttempts,RecoveryCommands=commands??new[] { "confirm","start_preparation","ready_to_pickup","dispatch" } },tx,cancellationToken:ct));
        var row=await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"""
WITH picked AS (
SELECT o.operation_id FROM ifood_outbound_commands o JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
WHERE o.state='queued' AND o.attempts<@Max AND o.next_attempt_at_utc<=clock_timestamp() AND o.not_before_utc<=@Now
AND o.command_type=ANY(@Commands) AND o.merchant_id=ANY(@Merchants)
AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
ORDER BY o.deadline_at_utc NULLS LAST,o.created_at_utc LIMIT 1 FOR UPDATE OF o SKIP LOCKED)
UPDATE ifood_outbound_commands o SET state='processing',attempts=attempts+1,lease_id=@Lease,lease_until_utc=clock_timestamp()+make_interval(secs=>@Seconds),updated_at_utc=NOW()
FROM picked WHERE o.operation_id=picked.operation_id RETURNING {string.Join(",",ReservationColumns.Split(',').Select(s=>"o."+s.Trim()))}
""",new { Max=_options.CommandMaxAttempts,Now=_clock.GetUtcNow(),Commands=commands??_options.AllowedCommands.ToArray(),
            Merchants=merchants??_options.CommandMerchants.Select(m=>m.MerchantId).ToArray(),Lease=Guid.NewGuid(),Seconds=_options.CommandLeaseSeconds },tx,cancellationToken:ct));
        if(row!=null) await Journal(c,tx,row,"claimed",null,null,ct);
        await tx.CommitAsync(ct);return row;
    });
    public Task MarkSendingAsync(IfoodCommandReservation item,CancellationToken ct=default) => Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockOrder(c,tx,item.MerchantId,item.ExternalOrderId,ct);
        var current=await Owned(c,tx,item,ct);
        if(current.SendStartedAtUtc!=null) throw new IfoodIntegrationException("IFOOD_COMMAND_ALREADY_SENT");
        await Authorize(c,tx,item.EstabelecimentoId,item.MerchantId,ct);
        var context=await Context(c,tx,item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,ct);
        var window=IfoodCommandRules.Validate(current.Command,context,current.ExpectedRevision,_clock.GetUtcNow(),_options);
        if(window.NotBeforeUtc>_clock.GetUtcNow()) throw IfoodCommandRules.Error("IFOOD_COMMAND_TOO_EARLY");
        var count=await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_outbound_commands SET send_started_at_utc=@Now,lease_until_utc=clock_timestamp()+make_interval(secs=>@Seconds),updated_at_utc=NOW()
WHERE operation_id=@OperationId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp() AND send_started_at_utc IS NULL
""",new { item.OperationId,item.LeaseId,Now=_clock.GetUtcNow(),Seconds=_options.CommandLeaseSeconds },tx,cancellationToken:ct));
        if(count!=1) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        await Journal(c,tx,current,"sending",null,null,ct);
        var live=await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE operation_id=@OperationId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp())",item,tx,cancellationToken:ct));
        if(!live) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        await tx.CommitAsync(ct);return true;
    });
    public Task CompleteAsync(IfoodCommandReservation item,IfoodCommandHttpResult response,CancellationToken ct=default)
        => Finish(item,response.State,response.Code,response.HttpStatus,response.RetryAfter,ct);
    public Task FailBeforeSendAsync(IfoodCommandReservation item,string code,bool retryable,CancellationToken ct=default,TimeSpan? retryAfter=null)
        => Finish(item,retryable?"retry":"rejected",code,null,retryAfter,ct);
    private Task Finish(IfoodCommandReservation item,string state,string? code,int? status,TimeSpan? retryAfter,CancellationToken ct) => Guard(async()=>
    {
        await using var c=await _source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await LockOrder(c,tx,item.MerchantId,item.ExternalOrderId,ct);
        var current=await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"SELECT {ReservationColumns} FROM ifood_outbound_commands WHERE operation_id=@OperationId AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId FOR UPDATE",item,tx,cancellationToken:ct));
        if(current==null) throw new IfoodIntegrationException("IFOOD_COMMAND_NOT_FOUND");
        // Evento pode confirmar/cancelar durante o HTTP. Não rebaixa resolução já persistida.
        if(current.State!="processing") { await tx.CommitAsync(ct);return true; }
        if(current.LeaseId!=item.LeaseId) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        if(current.ExternalOrderId!=item.ExternalOrderId || current.Command!=item.Command) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST");
        if(current.SendStartedAtUtc!=null && status==null && state!="uncertain") { state="uncertain";code="IFOOD_COMMAND_SEND_RESULT_UNKNOWN"; }
        if(state=="retry" && current.Attempts>=_options.CommandMaxAttempts) { state="review";code="IFOOD_COMMAND_RETRIES_EXHAUSTED"; }
        var delay=Math.Max(30,retryAfter?.TotalSeconds ?? 30*Math.Pow(2,Math.Min(current.Attempts-1,4)));
        if(state=="retry" && delay>TimeSpan.FromDays(30).TotalSeconds) { state="review";code="IFOOD_COMMAND_RETRY_DELAY_REQUIRES_REVIEW";delay=30; }
        var journal=state=="retry"?"retry":state=="accepted"?"accepted":state;
        var persisted=state=="retry"?"queued":state=="accepted"?"uncertain":state;
        var completed=await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_outbound_commands SET state=@State,lease_id=NULL,lease_until_utc=NULL,
send_started_at_utc=CASE WHEN @State='queued' THEN NULL ELSE send_started_at_utc END,
http_accepted_at_utc=CASE WHEN @Accepted THEN @Now ELSE http_accepted_at_utc END,
last_error_code=@Code,next_attempt_at_utc=clock_timestamp()+make_interval(secs=>@Delay),updated_at_utc=NOW(),
finished_at_utc=CASE WHEN @State IN('rejected','review') THEN NOW() ELSE NULL END,
result=jsonb_build_object('source','transport','code',@Code::text,'httpStatus',@Status::int)
WHERE operation_id=@OperationId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp()
""",new { item.OperationId,item.LeaseId,State=persisted,Code=code,Status=status,Delay=delay,Accepted=state=="accepted",Now=_clock.GetUtcNow() },tx,cancellationToken:ct));
        if(completed!=1) throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
        await Journal(c,tx,current,journal,code,status,ct);
        if(Exceptions.IfoodExceptionRules.IsDispute(current.Command) && persisted=="rejected")
            await ReleaseRejectedDispute(c,tx,current,ct);
        var context=await Context(c,tx,item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,ct);
        await Notify(c,tx,context,item.OperationId,persisted,ct);await tx.CommitAsync(ct);return true;
    });
    private static async Task<IfoodCommandReservation> Owned(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandReservation item,CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<IfoodCommandReservation>(new CommandDefinition($"""
SELECT {ReservationColumns} FROM ifood_outbound_commands WHERE operation_id=@OperationId AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
AND external_order_id=@ExternalOrderId AND command_type=@Command AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp() FOR UPDATE
""",item,tx,cancellationToken:ct)) ?? throw new IfoodIntegrationException("IFOOD_COMMAND_LEASE_LOST",retryable:true);
    private static Task LockOrder(NpgsqlConnection c,NpgsqlTransaction tx,Guid merchant,Guid order,CancellationToken ct)
        => c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))",new { Key=$"ifood:{merchant:D}:{order:D}" },tx,cancellationToken:ct));
    private static async Task Authorize(NpgsqlConnection c,NpgsqlTransaction tx,Guid store,Guid merchant,CancellationToken ct)
    {
        var found=await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
SELECT merchant_id FROM ifood_connections WHERE merchant_id=@Merchant AND estabelecimento_id=@Store
AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL FOR SHARE
""",new { Store=store,Merchant=merchant },tx,cancellationToken:ct));
        if(found==null) throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
    }
    private static async Task<IfoodCommandContext> Context(NpgsqlConnection c,NpgsqlTransaction tx,Guid merchant,Guid order,Guid store,CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<IfoodCommandContext>(new CommandDefinition("""
SELECT l.pedido_id AS PedidoId,s.merchant_id AS MerchantId,s.external_order_id AS ExternalOrderId,s.estabelecimento_id AS EstabelecimentoId,
s.revision AS Revision,s.remote_status AS RemoteStatus,s.preparation_state AS PreparationState,s.synchronization_state AS SynchronizationState,
s.cancellation_state AS CancellationState,s.data_is_current AS DataIsCurrent,s.payload::text AS Payload
FROM ifood_order_snapshots s JOIN ifood_order_links l USING(merchant_id,external_order_id,estabelecimento_id)
WHERE s.merchant_id=@Merchant AND s.external_order_id=@Order AND s.estabelecimento_id=@Store FOR UPDATE OF s
""",new { Merchant=merchant,Order=order,Store=store },tx,cancellationToken:ct)) ?? throw IfoodCommandRules.Error("IFOOD_COMMAND_SNAPSHOT_MISSING");
    internal static Task<int> Journal(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandReservation item,string stage,string? code,int? status,CancellationToken ct)
        => c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_command_attempts(operation_id,attempt_number,stage,code,http_status)VALUES(@OperationId,@Attempts,@Stage,@Code,@Status) ON CONFLICT DO NOTHING
""",new { item.OperationId,item.Attempts,Stage=stage,Code=code,Status=status },tx,cancellationToken:ct));
    internal static Task<int> Notify(NpgsqlConnection c,NpgsqlTransaction tx,IfoodCommandContext context,Guid operation,string state,CancellationToken ct)
        => c.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,motoboy_id,session_id,session_epoch,aggregate_version,payload,occurred_at_utc)
VALUES(@Id,@Name,@Group,@Store,NULL,NULL,NULL,@Revision,CAST(@Payload AS jsonb),NOW())
""",new { Id=Guid.NewGuid(),Name=DeliveryRealtimeEvents.DeliveryOrderUpdated,Group=DeliveryRealtimeEvents.EstablishmentGroup(context.EstabelecimentoId),
            Store=context.EstabelecimentoId,context.Revision,Payload=JsonSerializer.Serialize(new { schemaVersion=2,pedidoId=context.PedidoId,estabelecimentoId=context.EstabelecimentoId,action="ifood_command",operationId=operation,state }) },tx,cancellationToken:ct));
    private static string Hash(IfoodCommandContext identity,int user,IfoodCommandRequest request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { identity.MerchantId,identity.ExternalOrderId,identity.EstabelecimentoId,
            user,request.ExpectedRevision,request.Command,payload=IfoodCommandRules.Payload(request.Command) })))).ToLowerInvariant();
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch(PostgresException ex) when(ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn) { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch(PostgresException ex) when(ex.SqlState==PostgresErrorCodes.UniqueViolation) { throw IfoodCommandRules.Error("IFOOD_COMMAND_OPERATION_CONFLICT"); }
        catch(NpgsqlException) { throw new IfoodIntegrationException("IFOOD_COMMAND_DATABASE_ERROR",retryable:true); }
    }
}
