using System.Net;
using System.Text.Json.Nodes;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string CommandFile="20261013_07_ifood_outbound_commands.sql";
    private static async Task<Database> CommandDatabase(bool import=true)
    {
        var db=await ProjectionDatabase(import);
        try { await db.Migrate(CommandFile);return db; }
        catch { await db.DisposeAsync();throw; }
    }
    private static IfoodCommandRepository CommandRepo(Database db,IfoodOptions? options=null,IfoodCommandTests.Clock? clock=null)
        => new(db.Source,IfoodAuthenticationTests.Monitor(options??IfoodCommandTests.Options(db.MerchantA,db.StoreA)),clock??new());
    private static IfoodCommandRequest CommandRequest(string command="confirm",long revision=1,Guid? operation=null)
        => new() { Command=command,ExpectedRevision=revision,OperationId=operation??Guid.NewGuid() };
    private static IfoodCommandService CommandService(Database db,IfoodCommandRepository repo,IfoodAuthenticationTests.Handler handler,IfoodOptions options,IfoodCommandTests.Clock clock,Func<Task>? tokenAction=null)
    {
        var tokens=new Mock<IIfoodTokenProvider>();
        tokens.Setup(t=>t.GetAsync(It.IsAny<CancellationToken>())).Returns(async()=>
        {
            if(tokenAction!=null)await tokenAction();return new IfoodAccessToken("test-only-token",DateTimeOffset.UtcNow.AddHours(1),TimeSpan.FromHours(1));
        });
        var bound=IfoodAuthenticationTests.Monitor(options);
        var client=new IfoodCommandClient(new IfoodOrderImportTests.Factory(handler),new IfoodMerchantAccess(db.Connections,tokens.Object,bound),tokens.Object,clock);
        return new(repo,client,bound);
    }
    [DeliveryDatabaseFact]
    public async Task CommandIntentionIsIdempotentBoundToActorOrderPayloadAndTenantAndBlocksSecondUncertainOperation()
    {
        await using var db=await CommandDatabase();var repo=CommandRepo(db);var request=CommandRequest();
        await repo.EnqueueAsync(db.StoreA,1,100,request);await repo.EnqueueAsync(db.StoreA,1,100,request);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreA,2,100,request));
        request.ExpectedRevision=2;await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreA,1,100,request));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreB,1,100,CommandRequest()));
        Assert.Null(await repo.ReadAsync(db.StoreB,request.OperationId));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreA,1,100,CommandRequest()));
        var detail=await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,100);
        Assert.Equal("queued",detail!.IdentidadeExterna!.ActiveCommand!.State);Assert.Equal("pending",detail.IdentidadeExterna.SynchronizationState);Assert.Null(detail.PedidoIfood!.AmountToCollect);
        var item=(await repo.ClaimAsync())!;await repo.MarkSendingAsync(item);
        await repo.CompleteAsync(item,new("accepted","IFOOD_COMMAND_AWAITING_EVENT",202));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreA,1,100,CommandRequest()));
        Assert.Null(await repo.ClaimAsync());
    }
    [DeliveryDatabaseFact]
    public async Task CommandAccepted202WaitsForExactEventBeforeNextPreparationOrDispatchAndNeverPromotesLocalRoute()
    {
        await using var db=await CommandDatabase();var clock=new IfoodCommandTests.Clock();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);
        var repo=CommandRepo(db,options,clock);var handler=new IfoodAuthenticationTests.Handler { Send=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)) };
        var service=CommandService(db,repo,handler,options,clock);
        var commands=new[] { "confirm","start_preparation","ready_to_pickup","dispatch" };var events=new[] { "CFM","PRS","RTP","DSP" };
        for(var index=0;index<commands.Length;index++)
        {
            var revision=await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");var request=CommandRequest(commands[index],revision);
            await service.SubmitAsync(db.StoreA,1,100,request);Assert.Equal(1,await service.RunOnceAsync());
            Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
            Assert.Null(await repo.ClaimAsync());
            var ev=await ProjectionEvent(db,events[index],index+2);await Project(db,ev);
            var result=await repo.ReadAsync(db.StoreA,request.OperationId);
            Assert.Equal("succeeded",result!.State);Assert.Equal(ev.EventId,result.ConfirmedEventId);clock.Now=clock.Now.AddMinutes(1);
        }
        Assert.Equal(4,handler.Calls);Assert.Equal("DISPATCHED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL FROM pedido WHERE id>=100"));
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));
    }
    [DeliveryDatabaseFact]
    public async Task CommandConcurrentClaimsHaveOneOwnerAndCrashRecoveryDoesNotReplayStartedPost()
    {
        await using var db=await CommandDatabase();var repo=CommandRepo(db);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var claims=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>repo.ClaimAsync()));var first=Assert.Single(claims.Where(x=>x!=null))!;
        await db.Execute("UPDATE ifood_outbound_commands SET lease_until_utc=clock_timestamp()-INTERVAL '1 second'");
        var second=(await repo.ClaimAsync())!;Assert.Equal(2,second.Attempts);
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>repo.MarkSendingAsync(first));
        await repo.MarkSendingAsync(second);await db.Execute("UPDATE ifood_outbound_commands SET lease_until_utc=clock_timestamp()-INTERVAL '1 second'");
        Assert.Null(await repo.ClaimAsync());Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.Null(await repo.ClaimAsync());
    }
    [DeliveryDatabaseFact]
    public async Task CommandLateHttpResponseCannotDowngradeEventResolution()
    {
        await using var db=await CommandDatabase();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);var clock=new IfoodCommandTests.Clock();var repo=CommandRepo(db,options,clock);
        var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var handler=new IfoodAuthenticationTests.Handler { Send=async(_,_)=>
        {
            await Project(db,await ProjectionEvent(db,"CFM",2));return new(HttpStatusCode.Accepted);
        } };
        await CommandService(db,repo,handler,options,clock).RunOnceAsync();
        Assert.Equal("succeeded",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);Assert.Equal(1,handler.Calls);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_command_attempts WHERE stage='succeeded'"));
    }
    [DeliveryDatabaseFact]
    public async Task CommandOldMatchingEventCannotResolveNewerSendAndCancellationEndsOperation()
    {
        await using var db=await CommandDatabase();var clock=new IfoodCommandTests.Clock();var repo=CommandRepo(db,clock:clock);var request=CommandRequest();
        await repo.EnqueueAsync(db.StoreA,1,100,request);var item=(await repo.ClaimAsync())!;clock.Now=clock.Now.AddMinutes(3);
        await repo.MarkSendingAsync(item);await repo.CompleteAsync(item,new("uncertain","IFOOD_COMMAND_TIMEOUT"));
        await Project(db,await ProjectionEvent(db,"CFM",2));Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        await Project(db,await ProjectionEvent(db,"CAN",6));
        Assert.Equal("rejected",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);Assert.Equal(4,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
    }
    [DeliveryDatabaseFact]
    public async Task CommandVersionAndAuthorizationAreRecheckedAfterTokenBeforeAnyPost()
    {
        await using var db=await CommandDatabase();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);var clock=new IfoodCommandTests.Clock();var repo=CommandRepo(db,options,clock);
        var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);var handler=new IfoodAuthenticationTests.Handler();
        await CommandService(db,repo,handler,options,clock,()=>db.Execute("UPDATE ifood_order_snapshots SET revision=revision+1")).RunOnceAsync();
        Assert.Equal(0,handler.Calls);Assert.Equal("IFOOD_COMMAND_STALE_REVISION",(await repo.ReadAsync(db.StoreA,request.OperationId))!.LastErrorCode);
        var next=CommandRequest(revision:2);await repo.EnqueueAsync(db.StoreA,1,100,next);
        await CommandService(db,repo,handler,options,clock,()=>db.Execute("UPDATE ifood_connections SET status='revoked'")).RunOnceAsync();
        Assert.Equal(0,handler.Calls);Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED",(await repo.ReadAsync(db.StoreA,next.OperationId))!.LastErrorCode);
    }
    [DeliveryDatabaseFact]
    public async Task CommandSafe429RetryHonorsLongRetryAfterAnd5xxTimeoutRemainUncertain()
    {
        await using var db=await CommandDatabase();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);var clock=new IfoodCommandTests.Clock();var repo=CommandRepo(db,options,clock);
        var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var handler=new IfoodAuthenticationTests.Handler { Send=(_,_)=>
        {
            var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new(TimeSpan.FromDays(2));return Task.FromResult(response);
        } };
        var service=CommandService(db,repo,handler,options,clock);await service.RunOnceAsync();
        Assert.Equal("queued",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.True(await db.Scalar<bool>("SELECT next_attempt_at_utc>clock_timestamp()+INTERVAL '47 hours' FROM ifood_outbound_commands"));Assert.Equal(0,await service.RunOnceAsync());
        await db.Execute("UPDATE ifood_outbound_commands SET next_attempt_at_utc=clock_timestamp()-INTERVAL '1 second'");
        handler.Send=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));await service.RunOnceAsync();
        Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);Assert.Equal(0,await service.RunOnceAsync());Assert.Equal(2,handler.Calls);
    }
    [DeliveryDatabaseFact]
    public async Task CommandHttpAcceptanceWithLocalOutboxFailureRecoversUncertainAndDoesNotSendAgain()
    {
        await using var db=await CommandDatabase();var repo=CommandRepo(db);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var item=(await repo.ClaimAsync())!;await repo.MarkSendingAsync(item);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if09_outbox_failure CHECK(payload->>'state' IS DISTINCT FROM 'uncertain')");
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>repo.CompleteAsync(item,new("accepted","IFOOD_COMMAND_AWAITING_EVENT",202)));
        Assert.Equal("processing",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_command_attempts WHERE stage='accepted'"));
        await db.Execute("UPDATE ifood_outbound_commands SET lease_until_utc=clock_timestamp()-INTERVAL '1 second'");Assert.Null(await repo.ClaimAsync());
        Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if09_outbox_failure");
        await Project(db,await ProjectionEvent(db,"CFM",2));Assert.Equal("succeeded",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
    }
    [DeliveryDatabaseFact]
    public async Task CommandEnqueueOutboxFailureDoesNotLeaveIntentionOrPromotePedido()
    {
        await using var db=await CommandDatabase();var repo=CommandRepo(db);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if09_enqueue_failure CHECK(payload->>'action' IS DISTINCT FROM 'ifood_command')");
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>repo.EnqueueAsync(db.StoreA,1,100,CommandRequest()));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
    }
    [DeliveryDatabaseFact]
    public async Task CommandScheduledPrepWaitsForOfficialTimeAndMissingTimeDoesNotGetInvented()
    {
        await using var db=await CommandDatabase();var root=JsonNode.Parse(IfoodOrderImportTests.Payload(db.MerchantA,db.OrderA))!;
        root["orderTiming"]="SCHEDULED";root["schedule"]=JsonNode.Parse("{\"deliveryDateTimeStart\":\"2026-10-13T18:20:00Z\",\"deliveryDateTimeEnd\":\"2026-10-13T18:40:00Z\",\"preparationStartDateTime\":\"2026-10-13T18:10:00Z\"}");
        await db.Execute("UPDATE ifood_order_snapshots SET payload=CAST(@Payload AS jsonb),remote_status='CONFIRMED',preparation_state='accepted'",new { Payload=root.ToJsonString() });
        var clock=new IfoodCommandTests.Clock();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);var repo=CommandRepo(db,options,clock);
        var request=CommandRequest("start_preparation");await repo.EnqueueAsync(db.StoreA,1,100,request);Assert.Null(await repo.ClaimAsync());
        clock.Now=DateTimeOffset.Parse("2026-10-13T18:10:00Z");var item=(await repo.ClaimAsync())!;await repo.MarkSendingAsync(item);Assert.NotNull((await repo.ReadAsync(db.StoreA,request.OperationId))!.SendStartedAtUtc);
    }
    [DeliveryDatabaseFact]
    public async Task CommandConfirmationDeadlineRecheckedAfterQueueAndDispatchWaitsForReadyEvent()
    {
        await using var db=await CommandDatabase();var clock=new IfoodCommandTests.Clock();var repo=CommandRepo(db,clock:clock);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var item=(await repo.ClaimAsync())!;clock.Now=DateTimeOffset.Parse("2026-10-13T18:08:00Z");
        Assert.Equal("IFOOD_COMMAND_CONFIRMATION_DEADLINE",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.MarkSendingAsync(item))).Code);
        await repo.FailBeforeSendAsync(item,"IFOOD_COMMAND_CONFIRMATION_DEADLINE",false);
        await Project(db,await ProjectionEvent(db,"CFM",2));var revision=await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        Assert.Equal("IFOOD_COMMAND_READY_EVENT_REQUIRED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.EnqueueAsync(db.StoreA,1,100,CommandRequest("dispatch",revision)))).Code);
    }
    [DeliveryDatabaseFact]
    public async Task CommandEventReconciliationAndOrderProjectionRollbackTogetherOnOutboxFailure()
    {
        await using var db=await CommandDatabase();var repo=CommandRepo(db);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var item=(await repo.ClaimAsync())!;await repo.MarkSendingAsync(item);await repo.CompleteAsync(item,new("accepted","IFOOD_COMMAND_AWAITING_EVENT",202));
        var ev=await ProjectionEvent(db,"CFM",2);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if09_event_failure CHECK(aggregate_version=1)");
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,ev));Assert.Equal("uncertain",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_command_attempts WHERE stage='succeeded'"));
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if09_event_failure");await Project(db,ev);Assert.Equal("succeeded",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
    }
    [DeliveryDatabaseFact]
    public async Task CommandTimeoutIsNotReplayedAndExplicitUnauthorizedRetriesStopAtLimit()
    {
        await using var db=await CommandDatabase();var clock=new IfoodCommandTests.Clock();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);options.CommandMaxAttempts=2;
        var repo=CommandRepo(db,options,clock);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var handler=new IfoodAuthenticationTests.Handler { Send=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)) };
        var service=CommandService(db,repo,handler,options,clock);await service.RunOnceAsync();
        await db.Execute("UPDATE ifood_outbound_commands SET next_attempt_at_utc=clock_timestamp()-INTERVAL '1 second'");await service.RunOnceAsync();
        Assert.Equal("review",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);Assert.Equal(0,await service.RunOnceAsync());Assert.Equal(2,handler.Calls);
        // Resposta oficial posterior pode demonstrar o efeito e encerrar revisão sem novo POST.
        await Project(db,await ProjectionEvent(db,"CFM",2));Assert.Equal("succeeded",(await repo.ReadAsync(db.StoreA,request.OperationId))!.State);
        var next=CommandRequest("ready_to_pickup",await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots"));await repo.EnqueueAsync(db.StoreA,1,100,next);
        handler.Send=(_,_)=>throw new TaskCanceledException("private-error");await service.RunOnceAsync();
        Assert.Equal("IFOOD_COMMAND_TIMEOUT",(await repo.ReadAsync(db.StoreA,next.OperationId))!.LastErrorCode);Assert.Equal(0,await service.RunOnceAsync());Assert.Equal(3,handler.Calls);
    }
    [DeliveryDatabaseFact]
    public async Task CommandExpiredSendingTransactionCannotCommitMarkerAndReleasePost()
    {
        await using var db=await CommandDatabase();var options=IfoodCommandTests.Options(db.MerchantA,db.StoreA);options.CommandLeaseSeconds=1;
        var repo=CommandRepo(db,options);var request=CommandRequest();await repo.EnqueueAsync(db.StoreA,1,100,request);
        var item=(await repo.ClaimAsync())!;
        await db.Execute("""
CREATE FUNCTION if09_slow_sending() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.stage='sending' THEN PERFORM pg_sleep(2);END IF;RETURN NEW;END $$;
CREATE TRIGGER if09_slow_sending BEFORE INSERT ON ifood_command_attempts FOR EACH ROW EXECUTE FUNCTION if09_slow_sending();
""");
        Assert.Equal("IFOOD_COMMAND_LEASE_LOST",(await Assert.ThrowsAsync<IfoodIntegrationException>(()=>repo.MarkSendingAsync(item))).Code);
        Assert.Null((await repo.ReadAsync(db.StoreA,request.OperationId))!.SendStartedAtUtc);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_command_attempts WHERE stage='sending'"));
    }
    [DeliveryDatabaseFact]
    public async Task CommandMigrationIsRepeatableAndRollbackPreservesOperationsAndRequiresCorrectOrder()
    {
        await using var db=await CommandDatabase(false);await db.Migrate(CommandFile);
        var parent=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","rollback","20261013_06_ifood_order_projection.down.sql"));
        await Assert.ThrowsAsync<PostgresException>(()=>db.Execute(parent));
        var down=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","rollback","20261013_07_ifood_outbound_commands.down.sql"));
        await db.Execute(down);await db.Migrate(CommandFile);
        await db.Link(db.MerchantA,db.StoreA,1,db.OrderA);await db.Command(Guid.NewGuid(),db.MerchantA,db.StoreA,db.OrderA);
        await Assert.ThrowsAsync<PostgresException>(()=>db.Execute(down));Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido"));
    }
}
