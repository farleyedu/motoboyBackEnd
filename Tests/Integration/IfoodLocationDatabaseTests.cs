using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private static async Task<Database> LocationDatabase()
    {
        var db = await RoutingDatabase();
        try
        {
            await db.Migrate("20261008_02_delivery_completion.sql");
            await db.Migrate(OfficialCompletionFile);
            await db.Migrate(RuntimeSettingsFile);
            await db.Migrate(RuntimeFlagsFile);
            await db.Migrate(ReconciliationFile);
            await db.Migrate("20261015_02_ifood_command_panel.sql");
            await db.Migrate("20261018_02_ifood_delivery_location.sql");
            await db.Migrate("20261018_02_ifood_delivery_location.sql");
            await EnableRouting(db);
            await Project(db, await ProjectionEvent(db, "CFM"));
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static IfoodLocationRequest LocationRequest(long revision) => new()
    {
        ExpectedRevision = revision, Street = "Rua Corrigida", Number = "20", District = "Centro", City = "Uberlândia", State = "MG",
        PostalCode = "38400000", Latitude = -18.91, Longitude = -48.26, Note = "Ponto confirmado com o cliente", PointConfirmed = true
    };

    [DeliveryDatabaseFact]
    public async Task LocationCorrectionRepairsMapAndRoutingPreservesOriginalAndAuditsAtomically()
    {
        await using var db = await LocationDatabase();
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review',payload=jsonb_set(payload,'{delivery,deliveryAddress,coordinates}','null'::jsonb); UPDATE pedido SET latitude=NULL,longitude=NULL WHERE id=100");
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        var count = await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox");
        await new IfoodLocationService(db.Source).SaveAsync(db.StoreA, 7, 100, LocationRequest(revision), default);
        Assert.Equal(-18.91, await db.Scalar<double>("SELECT latitude FROM pedido WHERE id=100"));
        Assert.Equal("synced", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal("Rua Teste", await db.Scalar<string>("SELECT payload->'delivery'->'deliveryAddress'->>'streetName' FROM ifood_order_snapshots"));
        Assert.Equal("Rua Corrigida", await db.Scalar<string>("SELECT payload->'zippyDeliveryLocation'->'address'->>'streetName' FROM ifood_order_snapshots"));
        Assert.Equal(revision + 1, await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots"));
        Assert.Equal(7, await db.Scalar<int>("SELECT actor_user_id FROM ifood_reconciliation_audit WHERE action='adjust_location'"));
        Assert.True(await db.Scalar<bool>("SELECT details->'previousAddress' IS NOT NULL AND details->'address' IS NOT NULL FROM ifood_reconciliation_audit WHERE action='adjust_location'"));
        Assert.Equal(count + 1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Null(await RoutingBlock(db));
        var context = System.Text.Json.JsonSerializer.Serialize(await new IfoodLocationService(db.Source).GetAsync(db.StoreA, 100, default));
        Assert.Contains("Rua Corrigida", context); Assert.Contains("Rua Teste", context); Assert.Contains("manual", context);
    }

    [DeliveryDatabaseFact]
    public async Task LocationCorrectionDoesNotResolveFinancialOrEventReview()
    {
        await using var db = await LocationDatabase();
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review',payload=jsonb_set(payload,'{payments,pending}','null'::jsonb)");
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        await new IfoodLocationService(db.Source).SaveAsync(db.StoreA, 7, 100, LocationRequest(revision), default);
        Assert.Equal("review", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal("IFOOD_DELIVERY_REVIEW_REQUIRED", await RoutingBlock(db));
    }

    [DeliveryDatabaseFact]
    public async Task LocationCorrectionDoesNotBypassReviewedEventsAndZeroPointNeverEntersRoute()
    {
        await using var db = await LocationDatabase();
        await db.Execute("UPDATE pedido SET latitude=0,longitude=0 WHERE id=100");
        Assert.Equal("IFOOD_DELIVERY_LOCATION_REQUIRED", await RoutingBlock(db));
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review',payload=jsonb_set(payload,'{delivery,deliveryAddress,coordinates}','null'::jsonb); UPDATE ifood_events_inbox SET state='review',last_error_code='IFOOD_EVENT_CODE_CONFLICT'");
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        await new IfoodLocationService(db.Source).SaveAsync(db.StoreA, 7, 100, LocationRequest(revision), default);
        Assert.Equal("review", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal("IFOOD_DELIVERY_REVIEW_REQUIRED", await RoutingBlock(db));
    }

    [DeliveryDatabaseFact]
    public async Task LocationCorrectionRejectsOtherStoreStaleRevisionAndActiveRoute()
    {
        await using var db = await LocationDatabase(); var service = new IfoodLocationService(db.Source);
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        Assert.Equal(404, (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.SaveAsync(db.StoreB, 7, 100, LocationRequest(revision), default))).StatusCode);
        Assert.Equal("IFOOD_COMMAND_STALE_REVISION", (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.SaveAsync(db.StoreA, 7, 100, LocationRequest(revision - 1), default))).Code);
        await new PedidoQueueRepository(db.Source).AssignAsync(db.StoreA, 7, 1, 100);
        Assert.Equal("IFOOD_REFRESH_ACTIVE_ROUTE", (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.SaveAsync(db.StoreA, 7, 100, LocationRequest(revision), default))).Code);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_reconciliation_audit WHERE action='adjust_location'"));
    }

    [DeliveryDatabaseFact]
    public async Task OfficialRefreshPreservesManualPointAndOriginalRemoteAddress()
    {
        await using var db = await LocationDatabase();
        var revision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        await new IfoodLocationService(db.Source).SaveAsync(db.StoreA, 7, 100, LocationRequest(revision), default);
        var client = new Mock<IIfoodOrderDetailsClient>();
        client.Setup(x => x.GetAsync(It.IsAny<IfoodDetailsReservation>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA));
        var refresh = new IfoodOrderRefreshService(db.Source, new IfoodOrderImportRepository(db.Source), client.Object,
            IfoodAuthenticationTests.Monitor(new IfoodOptions { Enabled = true, ImportEnabled = true }));
        await refresh.RefreshAsync(db.StoreA, 7, 100, default);
        Assert.Equal(-18.91, await db.Scalar<double>("SELECT latitude FROM pedido WHERE id=100"));
        Assert.Equal("Rua Teste", await db.Scalar<string>("SELECT payload->'delivery'->'deliveryAddress'->>'streetName' FROM ifood_order_snapshots"));
        Assert.Equal("manual", await db.Scalar<string>("SELECT payload->'zippyDeliveryLocation'->>'source' FROM ifood_order_snapshots"));
        Assert.Null(await RoutingBlock(db));
    }
}
