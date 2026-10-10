using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
namespace APIBack.Tests.Integration;
public partial class DeliverySyncDatabaseTests
{
    private static Task ApplyCorrection(Database db,string name)=>db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Migrations","Delivery",name)));
    private const string Bag="""[{"nome":"Combo","quantidade":2,"adicionais":[{"id":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","nome":"Suco","quantidade":1},{"nome":"Brownie","quantidade":2}]}]""";
    private static DeliveryChecklistConfirmation Checked(int id,DeliveryChecklist checklist)=>new(){PedidoId=id,Version=checklist.Version,ConfirmedKeys=checklist.DetailsUnavailable?new(){"manual"}:checklist.Items.Select(i=>i.Key).ToList(),RecheckedExtraKeys=checklist.Items.Where(i=>i.Extra).Select(i=>i.Key).ToList()};
    [DeliveryDatabaseFact] public async Task ChecklistChangesRollBackPickupAndCompletionDoesNotChargeBeforeFinalConfirmation()
    {
        await using var db=await WorkDatabase();await WorkPlan(db);await CaptureWork(db);
        await ApplyCorrection(db,"20261009_01_delivery_order_checklists.sql");await ApplyCorrection(db,"20261009_01_delivery_order_checklists.sql");
        await db.Execute("""
CREATE TABLE cardapio_produto(id uuid,id_estabelecimento uuid,deleted_at timestamptz);
CREATE TABLE cardapio_grupo_adicional(id uuid,id_estabelecimento uuid,deleted_at timestamptz);
CREATE TABLE cardapio_grupo_adicional_item(id uuid,id_grupo uuid);
""");
        await ApplyCorrection(db,"20261010_02_cardapio_atencao_motoboy.sql");
        await db.Execute("INSERT INTO cardapio_grupo_adicional(id,id_estabelecimento,atencao_motoboy) VALUES('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',@StoreId,TRUE)",new{db.StoreId});
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL; UPDATE pedido SET items=@Bag WHERE id=23",new{Bag});
        var items=LegacyItemsParser.Parse(Bag);items[0].Adicionais[0].AtencaoMotoboy=true;
        var manifest=DeliveryChecklistRules.Build(items);var manual=DeliveryChecklistRules.Build(Array.Empty<PedidoItemDto>());
        var request=new PickupStopsRequest{ExpectedPedidoId=23,ExpectedVersion=3,PedidoIds=new(){23,24},Checklists=new(){Checked(23,manifest),Checked(24,manual)}};
        var repo=new PedidoQueueRepository(db.Source);request.Checklists[0].RecheckedExtraKeys.Clear();
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.PickUpStopsAsync(db.StoreId,1,request));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_order_item_checks"));Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops WHERE picked_up_at_utc IS NOT NULL"));
        request.Checklists[0]=Checked(23,manifest);var queue=await repo.PickUpStopsAsync(db.StoreId,1,request);
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_order_item_checks"));
        var completion=CompletionRequest();completion.ExpectedVersion=queue.Version;completion.Checklist=Checked(23,manifest);
        await db.Execute("UPDATE pedido SET items=replace(items,'Suco','Refrigerante') WHERE id=23");
        Assert.Equal("CHECKLIST_CHANGED",(await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,completion,default))).Code);
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
        await db.Execute("UPDATE pedido SET items=@Bag WHERE id=23",new{Bag});await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,completion,default);await repo.CompleteDeliveryAsync(db.StoreId,1,7,db.SessionId,4,completion,default);
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_order_item_checks WHERE stage='delivery'"));
    }
    [DeliveryDatabaseFact] public async Task OfferSelectionRejectsUnselectedOrdersAtomicallyAndPreservesAcceptedQuote()
    {
        await using var db=await WorkDatabase();await WorkPlan(db);await CaptureWork(db);await CaptureWork(db,2,24);
        var offer=Guid.NewGuid();await db.Execute("ALTER TABLE delivery_route_stops ADD COLUMN refused_at_utc timestamptz,ADD COLUMN refusal_reason text; UPDATE delivery_route_stops SET stop_status='assigned',offered_at_utc=NOW(),offer_id=@Offer;UPDATE delivery_settings SET require_motoboy_acceptance=TRUE;",new{Offer=offer});
        var repo=new PedidoQueueRepository(db.Source);
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.AcceptPricedOfferAsync(db.StoreId,1,offer,3,new[]{999}));
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>repo.AcceptPricedOfferAsync(db.StoreId,1,offer,2,new[]{24}));
        Assert.Equal(2,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops WHERE offered_at_utc IS NOT NULL"));
        var accepted=await repo.AcceptPricedOfferAsync(db.StoreId,1,offer,3,new[]{24});Assert.Equal(24,accepted.Current!.PedidoId);Assert.Empty(accepted.Next);Assert.Equal(8.34m,accepted.Current.Earnings!.Amount);
        Assert.Equal("refused",await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));Assert.Null(await db.Scalar<int?>("SELECT motoboy_responsavel FROM pedido WHERE id=23"));
        Assert.Equal(0,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
        await repo.AcceptPricedOfferAsync(db.StoreId,1,offer,3,new[]{24});Assert.Equal(24,(await repo.GetQueueAsync(db.StoreId,1)).Current!.PedidoId);
    }
    [DeliveryDatabaseFact] public async Task RouteHistoryArchivesOnlyOwnedGpsSurvivesSourceCleanupAndSplitsSignalGaps()
    {
        await using var db=await WorkDatabase();await WorkPlan(db);await CaptureWork(db);
        await ApplyCorrection(db,"20261009_01_delivery_order_checklists.sql");await ApplyCorrection(db,"20261009_04_route_history.sql");await ApplyCorrection(db,"20261009_04_route_history.sql");
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL;UPDATE delivery_route_stops SET picked_up_at_utc=NOW();UPDATE delivery_route_runs SET started_at_utc=NOW()-interval '10 minutes';");
        Assert.Equal(1,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_runs"));
        await db.Execute("""
INSERT INTO motoboy_location_samples(sample_id,session_id,motoboy_id,estabelecimento_id,latitude,longitude,quality,accuracy_meters,captured_at_utc)
 SELECT gen_random_uuid(),@SessionId,1,@StoreId,-18.918+v.n*.0001,-48.246,'good',8,NOW()-interval '9 minutes'+v.n*interval '20 seconds' FROM generate_series(1,2) v(n);
INSERT INTO motoboy_location_samples(sample_id,session_id,motoboy_id,estabelecimento_id,latitude,longitude,quality,accuracy_meters,captured_at_utc)
 VALUES(gen_random_uuid(),@SessionId,1,@StoreId,-18.916,-48.245,'good',8,NOW()-interval '1 minute'),(gen_random_uuid(),@SessionId,1,@StoreId,-18.91,-48.24,'low',500,NOW());
INSERT INTO motoboy_location_samples(sample_id,session_id,motoboy_id,estabelecimento_id,latitude,longitude,quality,accuracy_meters,captured_at_utc)
 VALUES(gen_random_uuid(),@SessionId,1,@Other,-18,-48,'good',8,NOW());
DELETE FROM motoboy_location_samples;
""",new{db.SessionId,db.StoreId,Other=Guid.NewGuid()});
        Assert.Equal(3,await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_run_points"));
        var history=new MotoboyRouteHistoryService(db.Source);var id=await db.Scalar<Guid>("SELECT id FROM delivery_route_runs");
        var json=JsonSerializer.SerializeToElement(await history.Detail(db.StoreId,1,id,default));Assert.Equal(2,json.GetProperty("Segments").GetArrayLength());Assert.True(json.GetProperty("PathAvailable").GetBoolean());Assert.Equal(2,json.GetProperty("Stops").GetArrayLength());Assert.DoesNotContain("NomeCliente",json.GetRawText());
        await Assert.ThrowsAsync<DeliveryDomainException>(()=>history.Detail(Guid.NewGuid(),1,id,default));await Assert.ThrowsAsync<DeliveryDomainException>(()=>history.Detail(db.StoreId,2,id,default));
        await db.Execute("UPDATE delivery_route_stops SET stop_status='completed';UPDATE delivery_motoboy_route SET route_state='idle';");Assert.NotNull(await db.Scalar<DateTimeOffset?>("SELECT ended_at_utc FROM delivery_route_runs"));
    }

    [DeliveryDatabaseFact]
    public async Task PendingOfferCanBeRefusedWithoutCreatingEarningsWhenAcceptedOrderReturnsAreDisabled()
    {
        await using var db = await WorkDatabase();
        await db.Execute("""
ALTER TABLE delivery_route_stops ADD COLUMN refused_at_utc timestamptz, ADD COLUMN refusal_reason text;
UPDATE delivery_settings SET require_motoboy_acceptance=TRUE, allow_motoboy_refuse=FALSE;
UPDATE delivery_route_stops SET stop_status='assigned', picked_up_at_utc=NULL, offered_at_utc=NOW(), offer_id=@Offer;
""", new { Offer = Guid.NewGuid() });
        var offer = await db.Scalar<Guid>("SELECT offer_id FROM delivery_route_stops LIMIT 1");
        var repo = new PedidoQueueRepository(db.Source);
        await repo.RejectOfferForAsync(db.StoreId, 1, offer, "Não consigo fazer esta rota.");
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops WHERE stop_status='refused'"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE motoboy_responsavel IS NOT NULL"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_rider_work_entries"));
    }

    private sealed class OfferPushHandler : HttpMessageHandler
    {
        public JsonElement? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://exp.host/--/api/v2/push/send", request.RequestUri!.ToString());
            Payload = JsonSerializer.SerializeToElement(await request.Content!.ReadFromJsonAsync<JsonElement>(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new[] { new { status = "ok", id = "test-ticket" } } })
            };
        }
    }

    [DeliveryDatabaseFact]
    public async Task OfferPushDeduplicatesLateRegistrationUsesPreferencesAndRecoversExhaustedLeases()
    {
        await using var db = await WorkDatabase();
        await db.Execute("""
CREATE TABLE delivery_chat_push_subscription(token text PRIMARY KEY, estabelecimento_id uuid NOT NULL,
 motoboy_id int NOT NULL, session_id uuid NOT NULL, sound bool NOT NULL, vibration bool NOT NULL);
UPDATE delivery_settings SET require_motoboy_acceptance=TRUE;
UPDATE delivery_route_stops SET stop_status='assigned', picked_up_at_utc=NULL, offered_at_utc=NOW(), offer_id=@Offer;
""", new { Offer = Guid.NewGuid() });
        await ApplyCorrection(db, "20261009_03_offer_push.sql");
        await ApplyCorrection(db, "20261009_03_offer_push.sql");
        await db.Execute("""
INSERT INTO delivery_chat_push_subscription VALUES('test-token',@StoreId,1,@SessionId,FALSE,TRUE);
UPDATE delivery_chat_push_subscription SET sound=FALSE;
""", new { db.StoreId, db.SessionId });
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_offer_push_outbox"));
        using var handler = new OfferPushHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        using var worker = new DeliveryOfferPushWorker(db.Source, factory.Object, new ConfigurationBuilder().Build(), NullLogger<DeliveryOfferPushWorker>.Instance);
        async Task Publish()
        {
            var method = typeof(DeliveryOfferPushWorker).GetMethod("PublishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(worker, new object[] { CancellationToken.None })!;
        }
        await Publish();
        Assert.Equal("sent", await db.Scalar<string>("SELECT state FROM delivery_offer_push_outbox"));
        var payload = handler.Payload!.Value[0];
        Assert.Equal("delivery-offers-silent-vibrate-v1", payload.GetProperty("channelId").GetString());
        Assert.Equal("high", payload.GetProperty("priority").GetString());
        Assert.Equal(db.SessionId, payload.GetProperty("data").GetProperty("recipientSessionId").GetGuid());
        Assert.InRange(payload.GetProperty("ttl").GetInt32(), 1, 300);
        await db.Execute("UPDATE delivery_offer_push_outbox SET state='sending',attempts=5,lease_until_utc=NOW()-interval '1 minute',lease_id=gen_random_uuid();");
        await Publish();
        Assert.Equal("failed", await db.Scalar<string>("SELECT state FROM delivery_offer_push_outbox"));
        Assert.Equal("ATTEMPTS_EXHAUSTED", await db.Scalar<string>("SELECT error_code FROM delivery_offer_push_outbox"));
        await db.Execute("UPDATE delivery_offer_push_outbox SET state='queued',attempts=0;UPDATE motoboy_active_sessions SET revoked_at=NOW();");
        await Publish();
        Assert.Equal("dismissed", await db.Scalar<string>("SELECT state FROM delivery_offer_push_outbox"));
        factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Once);
    }
}
