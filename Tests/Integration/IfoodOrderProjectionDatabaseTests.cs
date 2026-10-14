using System.Text.Json;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Tests.Unit;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string ProjectionFile="20261013_06_ifood_order_projection.sql";
    private static async Task<Database> ProjectionDatabase(bool import=true)
    {
        var db=await ImportDatabase();
        try
        {
            await db.Migrate(ProjectionFile);
            if(import)
            {
                var item=await ImportEvent(db,db.OrderA);
                await new IfoodOrderImportRepository(db.Source).ImportAsync(item,IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA,db.OrderA),db.MerchantA,db.OrderA));
            }
            return db;
        }
        catch { await db.DisposeAsync();throw; }
    }
    private static async Task<IfoodInboxReservation> ProjectionEvent(Database db,string code,int minute=1,object? metadata=null,Guid? order=null,Guid? merchant=null,Guid? store=null)
    {
        var id=Guid.NewGuid();var at=DateTimeOffset.Parse("2026-10-13T18:00:00Z").AddMinutes(minute);
        var body=JsonSerializer.Serialize(new { id,merchantId=merchant??db.MerchantA,orderId=order??db.OrderA,code,createdAt=at,metadata=metadata??new { } });
        await db.Execute("""
INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,external_order_id,event_code,payload,occurred_at_utc)
VALUES(@Merchant,@Store,@Event,@Order,@Code,CAST(@Payload AS jsonb),@At)
""",new { Merchant=merchant??db.MerchantA,Store=store??db.StoreA,Event=id,Order=order??db.OrderA,Code=code,Payload=body,At=at });
        return Assert.Single(await db.Inbox.ClaimAsync(1,90,8,default,new[] { code }));
    }
    private static Task<string> Project(Database db,IfoodInboxReservation item)=>new IfoodOrderProjectionRepository(db.Source).ProjectAsync(item);

    [DeliveryDatabaseFact]
    public async Task ProjectionSeparatesPreparationAndRemoteStatusAndNeverCreatesPhysicalDeliveryProof()
    {
        await using var db=await ProjectionDatabase();
        foreach(var code in new[] { "CFM","PRS","RTP","DSP","CON" })
            Assert.Equal("applied",await Project(db,await ProjectionEvent(db,code,Array.IndexOf(new[] { "CFM","PRS","RTP","DSP","CON" },code)+1)));
        Assert.Equal("CONCLUDED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal("ready",await db.Scalar<string>("SELECT preparation_state FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL AND codigo_entrega IS NULL FROM pedido WHERE id>=100"));
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));
        var detail=await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,100);
        Assert.Equal("CONCLUDED",detail!.IdentidadeExterna!.RemoteStatus);Assert.Null(detail.PedidoIfood!.AmountToCollect);
        Assert.Equal(5,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history"));
        Assert.Equal(6,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }
    [DeliveryDatabaseFact]
    public async Task CancelRequestAndFailureDoNotCancelButFinalCancelCannotBeUndoneByOldConfirmation()
    {
        await using var db=await ProjectionDatabase();
        await Project(db,await ProjectionEvent(db,"CAR",1));
        Assert.Equal("requested",await db.Scalar<string>("SELECT cancellation_state FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        await Project(db,await ProjectionEvent(db,"CARF",2));
        Assert.Equal("rejected",await db.Scalar<string>("SELECT cancellation_state FROM ifood_order_snapshots"));
        await Assert.ThrowsAsync<PostgresException>(()=>db.Execute("UPDATE pedido SET status_pedido=4 WHERE id>=100"));
        Assert.Equal("applied",await Project(db,await ProjectionEvent(db,"CAN",4)));
        Assert.Equal(4,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal("ignored",await Project(db,await ProjectionEvent(db,"CFM",3)));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"CFM",5)));
        Assert.Equal("CANCELLED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        await Assert.ThrowsAsync<PostgresException>(()=>db.Execute("UPDATE pedido SET status_pedido=1 WHERE id>=100"));
        await Assert.ThrowsAsync<PostgresException>(()=>db.Execute("UPDATE pedido SET motoboy_responsavel=42 WHERE id>=100"));
    }
    [DeliveryDatabaseFact]
    public async Task OldEventsDoNotProduceOutboxAndEqualTimestampConflictRequiresReview()
    {
        await using var db=await ProjectionDatabase();
        await Project(db,await ProjectionEvent(db,"RTP",4));
        Assert.Equal("ignored",await Project(db,await ProjectionEvent(db,"PRS",3)));
        Assert.Equal("ready",await db.Scalar<string>("SELECT preparation_state FROM ifood_order_snapshots"));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"PRS",4)));
        Assert.Equal("ready",await db.Scalar<string>("SELECT preparation_state FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task ConcurrentDifferentEventsSerializeAndReprocessingDoesNotDuplicateEffect()
    {
        await using var db=await ProjectionDatabase();
        var first=await ProjectionEvent(db,"CFM",1);var second=await ProjectionEvent(db,"DSP",2);
        await Task.WhenAll(Project(db,first),Project(db,second));
        Assert.Equal("DISPATCHED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history"));
        var count=await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox");
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,first));
        Assert.Equal(count,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }
    [DeliveryDatabaseFact]
    public async Task BeforePlacedEventWaitsForImportWithoutExhaustingTechnicalAttemptsAndWorkerExcludesPlaced()
    {
        await using var db=await ProjectionDatabase(false);
        var early=await ProjectionEvent(db,"CFM",2);
        Assert.Equal("waiting_import",await Project(db,early));
        Assert.Equal(0,await db.Scalar<int>("SELECT attempts FROM ifood_events_inbox"));
        var plc=await ImportEvent(db,db.OrderA);
        await new IfoodOrderImportRepository(db.Source).ImportAsync(plc,IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA,db.OrderA),db.MerchantA,db.OrderA));
        await db.Execute("UPDATE ifood_events_inbox SET next_attempt_at_utc=NOW()-INTERVAL '1 second' WHERE event_code='CFM'");
        var options=IfoodAuthenticationTests.EnabledOptions();options.ProjectionEnabled=true;
        var service=new IfoodOrderProjectionService(db.Inbox,new(db.Source),Microsoft.Extensions.Options.Options.Create(options));
        Assert.Equal(1,await service.RunOnceAsync());
        var another=await ImportEvent(db,db.OrderA);await db.Inbox.RetryEventAsync(another,8,TimeSpan.Zero);
        Assert.Equal(0,await service.RunOnceAsync());
        Assert.Equal("CONFIRMED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task OutboxFailureRollsBackCancelStatusSnapshotHistoryAndInboxThenCanRecover()
    {
        await using var db=await ProjectionDatabase();var item=await ProjectionEvent(db,"CAN",2);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if08_injected_failure CHECK(aggregate_version=1)");
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,item));
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history"));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE state='processing'"));
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if08_injected_failure");
        Assert.Equal("applied",await Project(db,item));
    }
    [DeliveryDatabaseFact]
    public async Task LeaseExpiringDuringTransactionRollsBackAllProjectionEffects()
    {
        await using var db=await ProjectionDatabase();var item=await ProjectionEvent(db,"CFM",1);
        await db.Execute("""
CREATE FUNCTION if08_slow_outbox() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(2);RETURN NEW;END $$;
CREATE TRIGGER if08_slow_outbox BEFORE INSERT ON delivery_realtime_outbox FOR EACH ROW EXECUTE FUNCTION if08_slow_outbox();
UPDATE ifood_events_inbox SET lease_until_utc=clock_timestamp()+INTERVAL '1 second' WHERE state='processing';
""");
        Assert.Equal("IFOOD_EVENT_LEASE_LOST",(await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,item))).Code);
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history"));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }
    [DeliveryDatabaseFact]
    public async Task PatchChangesItemsAndOfficialBalanceWhileIncompletePatchHidesStalePriceAndDetails()
    {
        await using var db=await ProjectionDatabase();
        Assert.Equal("applied",await Project(db,await ProjectionEvent(db,"OPA",1,IfoodOrderProjectionTests.Patch())));
        var read=new PedidoConsultaRepository(db.Source);var detail=await read.GetAsync(db.StoreA,100);
        Assert.Equal(70m,detail!.Total);Assert.Equal(50m,detail.PedidoIfood!.AmountToCollect);Assert.Equal(2,detail.PedidoIfood.Items.Count);
        var partial=JsonSerializer.SerializeToElement(new { changes=new[] { new { changeType="DELETE_ITEMS",items=new[] { new { id="second-item" } } } },total=new { subtotal=new { from=80m,to=77m } } });
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"OPA",2,partial)));
        detail=await read.GetAsync(db.StoreA,100);
        Assert.Null(detail!.Total);Assert.Null(detail.PedidoIfood);Assert.Empty(detail.Itens);Assert.Null(detail.Subtotal);Assert.Null(detail.TaxaEntrega);
        Assert.False(detail.IdentidadeExterna!.DataIsCurrent);Assert.Null(detail.IdentidadeExterna.PendingAmount);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_items"));
        Assert.Null(await read.GetAsync(db.StoreB,100));
        Assert.Equal("ignored",await Project(db,await ProjectionEvent(db,"OPA",1,IfoodOrderProjectionTests.Patch())));
    }
    [DeliveryDatabaseFact]
    public async Task UnknownCodeAndUnsupportedPatchStayReviewAndDoNotInventEffects()
    {
        await using var db=await ProjectionDatabase();
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"NEW_UNKNOWN",1,new { documentNumber="do-not-copy",future=true })));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"OPA",2,new { changes=new[] { new { changeType="UNKNOWN",items=Array.Empty<object>() } } })));
        Assert.True(await db.Scalar<bool>("SELECT NOT data_is_current AND order_amount IS NULL FROM ifood_order_snapshots"));
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history WHERE outcome='review'"));
        Assert.False(await db.Scalar<bool>("SELECT payload::text LIKE '%do-not-copy%' FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task SettlementBeforeDisputePreservesResolutionAndDoesNotInferCancellationOrRefund()
    {
        await using var db=await ProjectionDatabase();var id=Guid.NewGuid();
        Assert.Equal("applied",await Project(db,await ProjectionEvent(db,"HSS",3,new { disputeId=id,status="ACCEPTED" })));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_disputes"));
        Assert.Equal("applied",await Project(db,await ProjectionEvent(db,"HSD",1,new { disputeId=id,expiresAt="2026-10-13T18:02:00Z",timeoutAction="ACCEPT_CANCELLATION",action="CANCELLATION",documentNumber="private-document" })));
        Assert.Equal("resolved",await db.Scalar<string>("SELECT state FROM ifood_disputes"));
        Assert.Equal("ACCEPTED",await db.Scalar<string>("SELECT settlement->>'status' FROM ifood_disputes"));
        Assert.Equal("ACCEPT_CANCELLATION",await db.Scalar<string>("SELECT timeout_action FROM ifood_disputes"));
        Assert.False(await db.Scalar<bool>("SELECT payload::text LIKE '%private-document%' FROM ifood_disputes"));
        Assert.Equal("ignored",await Project(db,await ProjectionEvent(db,"HSS",2,new { disputeId=id,status="REJECTED" })));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"HSS",4,new { disputeId=id,status="REJECTED" })));
        Assert.Equal("resolved",await db.Scalar<string>("SELECT state FROM ifood_disputes"));
        Assert.Equal("ACCEPTED",await db.Scalar<string>("SELECT settlement->>'status' FROM ifood_disputes"));
        var detail=await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,100);
        Assert.Equal(id,Assert.Single(detail!.DisputasIfood).DisputeId);
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
        Assert.Equal(45m,await db.Scalar<decimal>("SELECT pending_amount FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task ExpiredDeadlineAndUnknownTimeoutArePreservedWithoutExecutingTimeoutAction()
    {
        await using var db=await ProjectionDatabase();var id=Guid.NewGuid();
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"HSD",1,new { disputeId=id,expiresAt="2026-10-13T18:02:00Z",timeoutAction="FUTURE_TIMEOUT",action="PARTIAL_CANCELLATION" })));
        Assert.Equal("FUTURE_TIMEOUT",await db.Scalar<string>("SELECT timeout_action FROM ifood_disputes"));
        Assert.Equal("review",await db.Scalar<string>("SELECT state FROM ifood_disputes"));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"HSD",2,new { disputeId=Guid.NewGuid(),action="CANCELLATION" })));
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_disputes"));
        Assert.Equal("PLACED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
    }
    [DeliveryDatabaseFact]
    public async Task MerchantRevocationAndWrongStoreReservationCannotProjectAndTrackingDoesNotAssignZippyDriver()
    {
        await using var db=await ProjectionDatabase();var item=await ProjectionEvent(db,"ADR",1,new { workerName="External",workerExternalUuid=Guid.NewGuid() });
        var actualStore=item.EstabelecimentoId;item.EstabelecimentoId=db.StoreB;
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,item));item.EstabelecimentoId=actualStore;
        await db.Execute("UPDATE ifood_connections SET status='revoked' WHERE merchant_id=@Merchant",new { Merchant=db.MerchantA });
        await Assert.ThrowsAsync<IfoodIntegrationException>(()=>Project(db,item));
        await db.Execute("UPDATE ifood_connections SET status='authorized' WHERE merchant_id=@Merchant",new { Merchant=db.MerchantA });
        Assert.Equal("applied",await Project(db,item));
        Assert.Equal("ASSIGN_DRIVER",await db.Scalar<string>("SELECT delivery_state FROM ifood_order_snapshots"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL FROM pedido WHERE id>=100"));
    }
    [DeliveryDatabaseFact]
    public async Task DisputeIdentityCannotMoveBetweenOrdersAndSettlementConflictCannotReopenResolvedDispute()
    {
        await using var db=await ProjectionDatabase();var id=Guid.NewGuid();
        await Project(db,await ProjectionEvent(db,"HSD",1,new { disputeId=id,expiresAt="2026-10-13T18:09:00Z",timeoutAction="VOID",action="CANCELLATION" }));
        await Project(db,await ProjectionEvent(db,"HSS",2,new { disputeId=id,status="REJECTED" }));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"HSD",3,new { disputeId=id,expiresAt="2026-10-13T18:09:00Z",timeoutAction="VOID",action="CANCELLATION" })));
        var plc=await ImportEvent(db,db.OrderB);
        await new IfoodOrderImportRepository(db.Source).ImportAsync(plc,IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA,db.OrderB),db.MerchantA,db.OrderB));
        Assert.Equal("review",await Project(db,await ProjectionEvent(db,"HSS",4,new { disputeId=id,status="ACCEPTED" },order:db.OrderB)));
        Assert.Equal("REJECTED",await db.Scalar<string>("SELECT settlement->>'status' FROM ifood_disputes"));
        Assert.Equal(0, (await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA,101))!.DisputasIfood.Count);
    }
    [DeliveryDatabaseFact]
    public async Task ReservationPayloadTamperingIsIgnoredAndOnlyDurableEnvelopeDrivesProjection()
    {
        await using var db=await ProjectionDatabase();var item=await ProjectionEvent(db,"CFM",1);
        item.PayloadJson="{\"code\":\"CAN\"}";item.EventCode="CAN";item.ExternalOrderId=db.OrderB;
        Assert.Equal("applied",await Project(db,item));
        Assert.Equal("CONFIRMED",await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(6,await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id>=100"));
    }
    [DeliveryDatabaseFact]
    public async Task ProjectionMigrationIsRepeatableRollbackRequiresEmptyEffectsAndCorrectDependencyOrder()
    {
        await using var db=await ProjectionDatabase(false);await db.Migrate(ProjectionFile);
        async Task<string> Down(string phase)=>await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery","rollback",phase));
        await Assert.ThrowsAsync<PostgresException>(async()=>await db.Execute(await Down("20261013_05_ifood_order_import.down.sql")));
        await db.Execute(await Down("20261013_06_ifood_order_projection.down.sql"));await db.Migrate(ProjectionFile);
        await Project(db,await ProjectionEvent(db,"UNKNOWN",1));
        await Assert.ThrowsAsync<PostgresException>(async()=>await db.Execute(await Down("20261013_06_ifood_order_projection.down.sql")));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido"));
    }
}
