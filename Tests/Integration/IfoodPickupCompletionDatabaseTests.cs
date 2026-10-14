using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private static async Task<Database> OfficialPickupDatabase()
    {
        var db = await RoutingDatabase();
        try
        {
            await db.Migrate("20261008_02_delivery_completion.sql"); await db.Migrate(OfficialCompletionFile);
            await db.Execute("UPDATE ifood_order_snapshots SET order_type='TAKEOUT',delivered_by=NULL,payload=CAST(@Payload AS jsonb)",
                new { Payload = IfoodOrderNormalizer.Sanitize(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA, "TAKEOUT"), db.MerchantA, db.OrderA) });
            await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2));
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<IfoodPickupCompletionRequest> OfficialPickupRequest(Database db) => new()
    {
        OperationId = Guid.NewGuid(), ExpectedRevision = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots"), Codigo = "client-test-code",
        Payments = new() { new() { Method = "dinheiro", Amount = 45, CashReceived = 50, ReceivedConfirmed = true } }
    };
    private static Task<IfoodCompletionOperationDto> EnqueueOfficialPickup(Database db, IfoodPickupCompletionRequest request) =>
        CompletionService(db, new()).EnqueuePickupAsync(db.StoreA, 7, 100, request, default);
    [DeliveryDatabaseFact]
    public async Task IfoodPickupCompletion_StorePickupIsIdempotentAndDoesNotCreateRiderDeliveryOrSession()
    {
        await using var db = await OfficialPickupDatabase(); var request = await OfficialPickupRequest(db);
        var service = CompletionService(db, new()); var context = await service.PickupContextAsync(db.StoreA, 100, default);
        Assert.Null(context.BlockedCode); Assert.Equal("TAKEOUT", context.IdentidadeExterna.OrderType);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => EnqueueOfficialPickup(db, request)));
        var client = new CompletionClient(); service = CompletionService(db, client); await service.ProcessAsync(default); await service.ProcessAsync(default);
        var operation = (await service.GetForStoreAsync(db.StoreA, request.OperationId, default))!;
        Assert.Equal("takeout", operation.Kind); Assert.Equal("succeeded", operation.State); Assert.Equal(1, client.Calls);
        Assert.Null(operation.Receipt); Assert.Equal(45m, operation.PickupReceipt!.AmountCollected); Assert.Equal(65m, operation.PickupReceipt.OfficialOrderTotal);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL AND horario_entrega IS NULL FROM pedido WHERE id=100"));
        Assert.Equal("succeeded", (await EnqueueOfficialPickup(db, request)).State);
        Assert.Null(await service.GetForStoreAsync(db.StoreB, request.OperationId, default));
        await OfficialTerminal(db, "CON"); Assert.Equal(3, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=100"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodPickupCompletion_UncertainRecoversByEventWithoutNewPostAndCancellationDoesNotCharge()
    {
        foreach (var terminal in new[] { "CON", "CAN" })
        {
            await using var db = await OfficialPickupDatabase(); var request = await OfficialPickupRequest(db); await EnqueueOfficialPickup(db, request);
            var client = new CompletionClient { Result = new("uncertain", "IFOOD_COMPLETION_REMOTE_RESULT_UNKNOWN") }; var service = CompletionService(db, client);
            await service.ProcessAsync(default); await service.ProcessAsync(default); await OfficialTerminal(db, terminal);
            var op = (await service.GetForStoreAsync(db.StoreA, request.OperationId, default))!;
            Assert.Equal(1, client.Calls); Assert.Equal(terminal == "CON" ? "succeeded" : "review", op.State);
            Assert.Equal(terminal == "CON" ? "pago" : "parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
            if (terminal == "CON") { Assert.False(op.PickupReceipt!.CodeChecked); Assert.Equal("concluded_event", op.PickupReceipt.OfficialConfirmationEvidence); }
            else Assert.Null(op.PickupReceipt);
        }
    }
    [DeliveryDatabaseFact]
    public async Task IfoodPickupCompletion_BlocksOtherStoreStaleRevisionWrongModalityAndBeforeReady()
    {
        await using var db = await OfficialPickupDatabase(); var request = await OfficialPickupRequest(db); var service = CompletionService(db, new());
        await Assert.ThrowsAsync<DeliveryDomainException>(() => service.EnqueuePickupAsync(db.StoreB, 7, 100, request, default));
        request.ExpectedRevision--;
        Assert.Equal("IFOOD_COMPLETION_STALE_REVISION", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficialPickup(db, request))).Code);
        request.ExpectedRevision++;
        foreach (var issue in new[] { "not_ready", "carrier", "cancellation", "revoked" })
        {
            await db.Execute("UPDATE ifood_order_snapshots SET order_type='TAKEOUT',preparation_state='ready',cancellation_state='none'; UPDATE ifood_connections SET status='authorized',revoked_at_utc=NULL");
            if (issue == "not_ready") await db.Execute("UPDATE ifood_order_snapshots SET preparation_state='preparing'");
            if (issue == "carrier") await db.Execute("UPDATE ifood_order_snapshots SET order_type='DELIVERY',delivered_by='IFOOD'");
            if (issue == "cancellation") await db.Execute("UPDATE ifood_order_snapshots SET cancellation_state='requested'");
            if (issue == "revoked") await db.Execute("UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW()");
            Assert.Equal("IFOOD_PICKUP_COMPLETION_NOT_ELIGIBLE", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficialPickup(db, request))).Code);
        }
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodPickupCompletion_FinalizationAndStoreOutboxAreAtomicAndRecoverable()
    {
        await using var db = await OfficialPickupDatabase(); var request = await OfficialPickupRequest(db); await EnqueueOfficialPickup(db, request);
        var repo = new PedidoQueueRepository(db.Source); var options = OfficialCompletionOptions(db);
        var reserved = (await repo.ClaimIfoodCompletionAsync(request.OperationId, options, default))!;
        await repo.MarkIfoodCompletionSendingAsync(reserved, options, default);
        await repo.SaveIfoodCompletionResultAsync(reserved, new("verified", "IFOOD_COMPLETION_CODE_CONFIRMED", 200), default);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if14_pickup_failure CHECK(payload->>'action' IS DISTINCT FROM 'ifood_pickup_confirmed')");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => repo.RecoverIfoodCompletionsAsync(default));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        Assert.Equal("verified", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if14_pickup_failure"); await repo.RecoverIfoodCompletionsAsync(default);
        Assert.Equal("succeeded", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
    }
}
