using APIBack.Integrations.Ifood;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Integrations.Ifood.Reconciliation;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task DeliveryCodeRequestIsInformationalAndDoesNotPutOrderInReview()
    {
        await using var db = await ProjectionDatabase();
        Assert.Equal("ignored", await Project(db, await ProjectionEvent(db, "DDCR")));
        Assert.Equal("synced", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
    }

    [DeliveryDatabaseFact]
    public async Task RefreshOfficialDetailsRepairsDataWithoutInventingStatusAndIsAudited()
    {
        await using var db = await RoutingDatabase();
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.Migrate(ReconciliationFile);
        await db.Migrate("20261015_02_ifood_command_panel.sql");
        await db.Migrate("20261015_02_ifood_command_panel.sql");
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review',payload=jsonb_set(payload,'{payments,pending}','null'::jsonb)");
        var client = new Mock<IIfoodOrderDetailsClient>();
        client.Setup(x => x.GetAsync(It.IsAny<IfoodDetailsReservation>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA));
        var options = new IfoodOptions { Enabled = true, ImportEnabled = true };
        var service = new IfoodOrderRefreshService(db.Source, new IfoodOrderImportRepository(db.Source), client.Object, IfoodAuthenticationTests.Monitor(options));
        await Assert.ThrowsAsync<DeliveryDomainException>(() => service.RefreshAsync(db.StoreB, 1, 100, default));
        await service.RefreshAsync(db.StoreA, 1, 100, default);
        Assert.Equal("PLACED", await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal("synced", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal(45m, await db.Scalar<decimal>("SELECT pending_amount FROM ifood_order_snapshots"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_reconciliation_audit WHERE action='refresh_details'"));
        Assert.Equal(6, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=100"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Migrations", "Delivery", "rollback", "20261015_02_ifood_command_panel.down.sql"))));
    }

    [DeliveryDatabaseFact]
    public async Task CommandVisibilityDoesNotRequireRoutingOrAddressAndDoesNotMutateLocalStatus()
    {
        await using var db = await RoutingDatabase();
        foreach (var mode in new[] { "DELIVERY", "TAKEOUT", "DINE_IN" })
        {
            await db.Execute("UPDATE ifood_order_snapshots SET order_type=@Mode; UPDATE pedido SET latitude=NULL,longitude=NULL WHERE id=100", new { Mode = mode });
            var visible = await db.Scalar<int>($"SELECT COUNT(*)::int FROM pedido p WHERE p.id_estabelecimento=@Store AND {TrackingRepository.IfoodCommandVisibilitySql}",
                new { Store = db.StoreA, WindowFromUtc = DateTimeOffset.UtcNow.AddHours(-1) });
            Assert.Equal(1, visible);
            Assert.Equal(6, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=100"));
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => new PedidoQueueRepository(db.Source).AssignAsync(db.StoreA, 1, 1, 100));
            Assert.Equal(409, error.StatusCode);
            Assert.Equal(mode == "DELIVERY" ? "IFOOD_DELIVERY_DISABLED" : "IFOOD_DELIVERY_NOT_APPLICABLE", error.Code);
            var http = new DefaultHttpContext(); http.Items["EstabelecimentoId"] = db.StoreA; http.Items["UserId"] = 1;
            var controller = new DeliveryOrdersV2Controller(new PedidoQueueService(new PedidoQueueRepository(db.Source)),
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!) {
                ControllerContext = new ControllerContext { HttpContext = http } };
            var response = Assert.IsAssignableFrom<ObjectResult>(await controller.Assign(100, new AssignPedidoRequest { MotoboyId = 1 }));
            Assert.Equal(409, response.StatusCode);
            Assert.Equal(error.Code, Assert.IsType<ApiResponse<object>>(response.Value).Code);
        }
        Assert.Equal(0, await db.Scalar<int>($"SELECT COUNT(*)::int FROM pedido p WHERE p.id_estabelecimento=@Store AND {TrackingRepository.IfoodCommandVisibilitySql}",
            new { Store = db.StoreB, WindowFromUtc = DateTimeOffset.UtcNow.AddHours(-1) }));
        await db.Execute("UPDATE ifood_order_snapshots SET remote_status='CONCLUDED',status_occurred_at_utc=NOW()-INTERVAL '10 days'; UPDATE ifood_order_links SET created_at_utc=NOW()-INTERVAL '10 days'");
        Assert.Equal(0, await db.Scalar<int>($"SELECT COUNT(*)::int FROM pedido p WHERE {TrackingRepository.IfoodCommandVisibilitySql}",
            new { WindowFromUtc = DateTimeOffset.UtcNow.AddDays(-1) }));
    }

    [DeliveryDatabaseFact]
    public async Task RecoveryImportsWithoutPlacedEventAndReplaysConfirmationBeforeEnablingCommands()
    {
        await using var db = await ProjectionDatabase(false);
        var item = await ProjectionEvent(db, "CFM");
        await db.Execute("UPDATE ifood_events_inbox SET state='queued',lease_id=NULL,lease_until_utc=NULL");
        var payload = IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA);
        var details = new Mock<IIfoodOrderDetailsClient>();
        details.Setup(x => x.GetAsync(It.IsAny<IfoodDetailsReservation>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>())).ReturnsAsync(payload);
        var options = new IfoodOptions { Enabled = true, ImportEnabled = true, ProjectionEnabled = true };
        var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<IfoodOptions>>();
        monitor.SetupGet(x => x.CurrentValue).Returns(options);
        Assert.Equal(1, await new IfoodOrderImportService(db.Inbox, new IfoodOrderImportRepository(db.Source), details.Object, monitor.Object).RunOnceAsync());
        Assert.Equal("pending", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.True(await db.Scalar<bool>("SELECT payload->'zippyRecovery'='true'::jsonb FROM ifood_order_snapshots"));
        Assert.Equal("queued", await db.Scalar<string>("SELECT state FROM ifood_events_inbox WHERE id=@Id", new { item.Id }));
        Assert.Equal(1, await new IfoodOrderProjectionService(db.Inbox, new IfoodOrderProjectionRepository(db.Source), monitor.Object).RunOnceAsync());
        Assert.Equal("CONFIRMED", await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal("synced", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links"));
    }

    [DeliveryDatabaseFact]
    public async Task DisputeAlertSurvivesTerminalOrderAndRemainsScopedToStore()
    {
        await using var db = await ReconciliationDatabase();
        await db.Execute("""
UPDATE ifood_order_snapshots SET remote_status='CONCLUDED',status_occurred_at_utc=NOW()-INTERVAL '5 days';
INSERT INTO ifood_disputes(merchant_id,estabelecimento_id,external_order_id,dispute_id,state,expires_at_utc,timeout_action,payload,opened_at_utc)
VALUES(@Merchant,@Store,@Order,gen_random_uuid(),'open',NOW()+INTERVAL '10 minutes','REJECT_CANCELLATION','{}'::jsonb,NOW());
""", new { Merchant = db.MerchantA, Store = db.StoreA, Order = db.OrderA });
        var repo = new IfoodReconciliationRepository(db.Source);
        var alert = Assert.Single((await repo.GetStoreAlertsAsync(db.StoreA)).Where(x => x.Code == "IFOOD_DISPUTE_OPEN"));
        Assert.Equal(100, alert.PedidoId); Assert.NotNull(alert.ExpiresAtUtc);
        Assert.DoesNotContain(await repo.GetStoreAlertsAsync(db.StoreB), x => x.Code == "IFOOD_DISPUTE_OPEN");
    }
}
