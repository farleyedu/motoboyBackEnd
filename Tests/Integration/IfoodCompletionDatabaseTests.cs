using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Completion;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string OfficialCompletionFile = "20261014_03_ifood_delivery_completion.sql";
    private static async Task<Database> OfficialCompletionDatabase()
    {
        var db = await RoutingDatabase();
        try
        {
            await db.Execute("ALTER TABLE motoboy_active_sessions ADD COLUMN id_usuario int DEFAULT 7,ADD COLUMN session_epoch bigint DEFAULT 4;");
            await db.Migrate("20261008_02_delivery_completion.sql");
            await db.Migrate(OfficialCompletionFile);
            await EnableRouting(db);
            await Project(db, await ProjectionEvent(db, "CFM"));
            await Project(db, await ProjectionEvent(db, "RTP", 2));
            await Project(db, await ProjectionEvent(db, "DSP", 3));
            var queue = new PedidoQueueRepository(db.Source);
            await queue.AssignAsync(db.StoreA, 7, 1, 100); var accepted = await queue.AcceptOfferAsync(db.StoreA, 1);
            var manifest = DeliveryChecklistRules.BuildIfood(accepted.Current!.Pedido!.PedidoIfood!);
            await queue.PickUpStopsAsync(db.StoreA, 1, new() { ExpectedPedidoId = 100, ExpectedVersion = accepted.Version, PedidoIds = new() { 100 },
                Checklists = new() { ConfirmedChecklist(manifest) } });
            await queue.MarkArrivedForPedidoAsync(db.StoreA, 1, 100);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static DeliveryChecklistConfirmation ConfirmedChecklist(DeliveryChecklist manifest) => new()
    { PedidoId = 100, Version = manifest.Version, ConfirmedKeys = manifest.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() };
    private static IfoodOptions OfficialCompletionOptions(Database db)
    { var o = IfoodCommandTests.Options(db.MerchantA, db.StoreA); o.CompletionEnabled = true; return o; }
    private static IfoodSecretProtector CompletionSecrets(IfoodOptions options) => new(Microsoft.Extensions.Options.Options.Create(options));
    private static async Task<DeliveryCompletionRequest> OfficialCompletionRequest(Database db)
    {
        var context = await new PedidoQueueRepository(db.Source).GetCompletionContextAsync(db.StoreA, 1, 100, default);
        return new() { OperationId = Guid.NewGuid(), ExpectedPedidoId = 100, ExpectedVersion = context.Version, Codigo = "client-test-code",
            Checklist = ConfirmedChecklist(context.Checklist), Payments = new() { new() { Method = "dinheiro", Amount = 45, CashReceived = 50, ReceivedConfirmed = true } } };
    }
    private static async Task<IfoodCompletionOperationDto> EnqueueOfficial(Database db, DeliveryCompletionRequest request, IfoodOptions? options = null)
    {
        options ??= OfficialCompletionOptions(db);
        return await new PedidoQueueRepository(db.Source).EnqueueIfoodCompletionAsync(db.StoreA, 1, 7,
            await db.Scalar<Guid>("SELECT session_id FROM motoboy_active_sessions"), 4, request, options, CompletionSecrets(options), default);
    }
    private sealed class CompletionClient : IIfoodDeliveryCodeClient
    {
        public int Calls;
        public IfoodDeliveryCodeResult Result = new("verified", "IFOOD_COMPLETION_CODE_CONFIRMED", 200);
        public Func<Task>? DuringSend;
        public async Task<IfoodDeliveryCodeResult> SendAsync(Guid store, Guid merchant, Guid order, string code, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
        {
            await beforeSend(ct); Interlocked.Increment(ref Calls); Assert.Equal("client-test-code", code);
            if (DuringSend != null) await DuringSend();
            return Result;
        }
    }
    private static IfoodCompletionService CompletionService(Database db, CompletionClient client, IfoodOptions? options = null)
    {
        options ??= OfficialCompletionOptions(db);
        return new(db.Source, IfoodAuthenticationTests.Monitor(options), CompletionSecrets(options), client);
    }
    private static async Task OfficialTerminal(Database db, string code)
    {
        var item = await ProjectionEvent(db, code, 4);
        await db.Execute("UPDATE ifood_events_inbox SET occurred_at_utc=clock_timestamp(),payload=jsonb_set(payload,'{createdAt}',to_jsonb(clock_timestamp())) WHERE id=@Id", new { item.Id });
        await Project(db, item);
    }

    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_OfficialContextUsesPendingBalanceAndNeverChecksCodeLocally()
    {
        await using var db = await OfficialCompletionDatabase(); var repo = new PedidoQueueRepository(db.Source);
        var context = await repo.GetCompletionContextAsync(db.StoreA, 1, 100, default);
        Assert.Equal("ifood", context.Origin); Assert.True(context.RequiresOfficialConfirmation); Assert.True(context.RequiresCode);
        Assert.Equal(45m, context.Total); Assert.Equal("001234", context.IdentidadeExterna!.DisplayNumber);
        Assert.Equal("IFOOD_COMPLETION_DISABLED", context.CompletionBlockedCode);
        await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.ValidateCompletionCodeAsync(db.StoreA, 1, 100, "anything", default));
        await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.CompleteDeliveryAsync(db.StoreA, 1, 7, Guid.NewGuid(), 4, new() { OperationId = Guid.NewGuid(), ExpectedPedidoId = 100, ExpectedVersion = context.Version }, default));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_EnqueueIsEncryptedIdempotentAndRecoveryIsOwnedByAccount()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        var operations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => EnqueueOfficial(db, request)));
        Assert.All(operations, o => Assert.Equal("queued", o.State));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_delivery_completions"));
        Assert.DoesNotContain(request.Codigo!, await db.Scalar<string>("SELECT row_to_json(o)::text FROM ifood_delivery_completions o"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        var service = CompletionService(db, new()); Assert.Null(await service.GetAsync(8, request.OperationId, default));
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW()");
        Assert.Equal("queued", (await service.GetAsync(7, request.OperationId, default))!.State);
        request.Codigo = "other-code";
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_SuccessCreatesOneReceiptAndPreservesOfficialPaymentAndLaterEvent()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await EnqueueOfficial(db, request); var client = new CompletionClient(); var service = CompletionService(db, client);
        await service.ProcessAsync(default); await service.ProcessAsync(default);
        var operation = (await service.GetAsync(7, request.OperationId, default))!;
        Assert.Equal("succeeded", operation.State); Assert.True(operation.CodeChecked); Assert.Equal(1, client.Calls);
        Assert.Equal(65m, operation.Receipt!.OfficialOrderTotal); Assert.Equal(45m, operation.Receipt.Total);
        Assert.Equal("verify_delivery_code", operation.Receipt.OfficialConfirmationEvidence);
        Assert.Equal("001234", operation.Receipt.ExternalDisplayNumber);
        Assert.Equal("pago", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        Assert.True(await db.Scalar<bool>("SELECT code_envelope IS NULL FROM ifood_delivery_completions"));
        Assert.Equal("succeeded", (await EnqueueOfficial(db, request)).State);
        await OfficialTerminal(db, "CON");
        Assert.Equal(3, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=100"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_UncertainNeverResendsAndConcludedEventRecoversWithoutClaimingCodeValidation()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await EnqueueOfficial(db, request); var client = new CompletionClient { Result = new("uncertain", "IFOOD_COMPLETION_REMOTE_RESULT_UNKNOWN") };
        var service = CompletionService(db, client); await service.ProcessAsync(default); await service.ProcessAsync(default);
        Assert.Equal(1, client.Calls); Assert.Equal("uncertain", (await service.GetAsync(7, request.OperationId, default))!.State);
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        await OfficialTerminal(db, "CON");
        var recovered = (await service.GetAsync(7, request.OperationId, default))!;
        Assert.Equal("succeeded", recovered.State); Assert.False(recovered.CodeChecked);
        Assert.Equal("concluded_event", recovered.Receipt!.OfficialConfirmationEvidence); Assert.Equal(1, client.Calls);
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_EventDuringHttpCannotBeDowngradedByLateResponse()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await EnqueueOfficial(db, request);
        var client = new CompletionClient { Result = new("uncertain", "IFOOD_COMPLETION_REMOTE_RESULT_UNKNOWN"), DuringSend = () => OfficialTerminal(db, "CON") };
        var service = CompletionService(db, client); await service.ProcessAsync(default);
        Assert.Equal("succeeded", (await service.GetAsync(7, request.OperationId, default))!.State);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_CancellationDuringHttpDoesNotCreateReceiptOrPayment()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await EnqueueOfficial(db, request);
        var client = new CompletionClient { DuringSend = () => OfficialTerminal(db, "CAN") };
        var service = CompletionService(db, client); await service.ProcessAsync(default);
        Assert.Equal("review", (await service.GetAsync(7, request.OperationId, default))!.State);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        Assert.Equal("canceled", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_InvalidCodeAllowsNewCorrectedOperationOnlyAfterDefinitiveRejection()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await EnqueueOfficial(db, request);
        var client = new CompletionClient { Result = new("rejected", "IFOOD_DELIVERY_CODE_INVALID", 200) };
        var service = CompletionService(db, client); await service.ProcessAsync(default);
        Assert.Equal("rejected", (await service.GetAsync(7, request.OperationId, default))!.State);
        request.OperationId = Guid.NewGuid(); await EnqueueOfficial(db, request);
        client.Result = new("verified", "IFOOD_COMPLETION_CODE_CONFIRMED", 200); await service.ProcessAsync(default);
        Assert.Equal("succeeded", (await service.GetAsync(7, request.OperationId, default))!.State); Assert.Equal(2, client.Calls);
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_CrashAfterVerifiedRecoversEvenWithDispatchDisabledAndLocalFailureRollsBack()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        var repo = new PedidoQueueRepository(db.Source); var options = OfficialCompletionOptions(db);
        await EnqueueOfficial(db, request);
        var reservation = (await repo.ClaimIfoodCompletionAsync(request.OperationId, options, default))!;
        await repo.MarkIfoodCompletionSendingAsync(reservation, options, default);
        await repo.SaveIfoodCompletionResultAsync(reservation, new("verified", "IFOOD_COMPLETION_CODE_CONFIRMED", 200), default);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if14_failure CHECK(payload->>'action' IS DISTINCT FROM 'delivered')");
        await Assert.ThrowsAsync<PostgresException>(() => repo.RecoverIfoodCompletionsAsync(default));
        Assert.Equal("verified", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if14_failure; UPDATE motoboy_active_sessions SET ended_at_utc=NOW()");
        options.CompletionEnabled = false; await CompletionService(db, new(), options).ProcessAsync(default);
        Assert.Equal("succeeded", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_CrashBeforeAndAfterSendHaveDifferentRecoveryAndCodesExpire()
    {
        foreach (var afterSend in new[] { false, true })
        {
            await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
            var repo = new PedidoQueueRepository(db.Source); var options = OfficialCompletionOptions(db); await EnqueueOfficial(db, request);
            var reservation = (await repo.ClaimIfoodCompletionAsync(request.OperationId, options, default))!;
            if (afterSend) await repo.MarkIfoodCompletionSendingAsync(reservation, options, default);
            await db.Execute("UPDATE ifood_delivery_completions SET lease_until_utc=NOW()-INTERVAL '1 second'");
            await repo.RecoverIfoodCompletionsAsync(default);
            Assert.Equal(afterSend ? "uncertain" : "queued", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
            await db.Execute("UPDATE ifood_delivery_completions SET expires_at_utc=NOW()-INTERVAL '1 second'");
            await repo.RecoverIfoodCompletionsAsync(default);
            Assert.Equal(afterSend ? "uncertain" : "rejected", (await repo.GetIfoodCompletionAsync(7, request.OperationId, default))!.State);
            Assert.True(await db.Scalar<bool>("SELECT code_envelope IS NULL FROM ifood_delivery_completions"));
        }
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_FlagsSessionSnapshotAndPaymentAreRevalidatedBeforeSend()
    {
        foreach (var change in new[] { "revocation", "snapshot", "session", "capability" })
        {
            await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
            await EnqueueOfficial(db, request); var client = new CompletionClient(); var options = OfficialCompletionOptions(db);
            if (change == "revocation") await db.Execute("UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW()");
            if (change == "snapshot") await db.Execute("UPDATE ifood_order_snapshots SET revision=revision+1");
            if (change == "session") await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW()");
            if (change == "capability") options.CommandMerchants.Clear();
            var service = CompletionService(db, client, options); await service.ProcessAsync(default);
            Assert.Equal(0, client.Calls); Assert.Equal("rejected", (await service.GetAsync(7, request.OperationId, default))!.State);
        }
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_PendingBlocksRouteMutationAndSecondOperationAndMigrationPreservesEvidence()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        await db.Migrate(OfficialCompletionFile); await EnqueueOfficial(db, request);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE delivery_route_stops SET stop_status='failed'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE delivery_route_stops SET stop_status='completed'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET status_pedido=1 WHERE id=100"));
        var context = await new PedidoQueueRepository(db.Source).GetCompletionContextAsync(db.StoreA, 1, 100, default);
        Assert.False(context.IdentidadeExterna!.CanAssignDelivery); Assert.NotNull(context.ExternalOperation);
        request.OperationId = Guid.NewGuid();
        Assert.Equal("IFOOD_COMPLETION_ACTIVE_OPERATION", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261014_03_ifood_delivery_completion.down.sql"))));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_UnsentConcludedEventDoesNotFabricateLocalDelivery()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db); await EnqueueOfficial(db, request);
        await OfficialTerminal(db, "CON");
        Assert.Equal("review", (await CompletionService(db, new()).GetAsync(7, request.OperationId, default))!.State);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_ConcurrentWorkersIssueOnlyOnePostAndOneReceipt()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db); await EnqueueOfficial(db, request);
        var client = new CompletionClient { DuringSend = () => Task.Delay(100) };
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CompletionService(db, client).ProcessAsync(default)));
        Assert.Equal(1, client.Calls); Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_RejectsWrongStoreVersionPaymentProofAndCodeWithoutPersistingIntent()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db);
        var options = OfficialCompletionOptions(db); var repo = new PedidoQueueRepository(db.Source); var session = await db.Scalar<Guid>("SELECT session_id FROM motoboy_active_sessions");
        await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.EnqueueIfoodCompletionAsync(db.StoreB, 1, 7, session, 4, request, options, CompletionSecrets(options), default));
        request.ExpectedVersion--;
        Assert.Equal("QUEUE_VERSION_CONFLICT", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        request.ExpectedVersion++; request.Payments[0].Amount = 65;
        Assert.Equal("PAYMENT_TOTAL_MISMATCH", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        request.Payments[0].Amount = 45; request.Payments[0].Method = "pix"; request.Payments[0].CashReceived = null;
        Assert.Equal("IFOOD_PAYMENT_METHOD_MISMATCH", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        request.Payments[0].Method = "dinheiro"; request.Payments[0].CashReceived = 50; request.ProofId = Guid.NewGuid();
        Assert.Equal("PROOF_INVALID", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        request.ProofId = null; request.Codigo = "";
        Assert.Equal("IFOOD_DELIVERY_CODE_REQUIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => EnqueueOfficial(db, request))).Code);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_PreSendFenceRechecksRevisionAndExpiredLease()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db); await EnqueueOfficial(db, request);
        var options = OfficialCompletionOptions(db); var repo = new PedidoQueueRepository(db.Source);
        var reservation = (await repo.ClaimIfoodCompletionAsync(request.OperationId, options, default))!;
        await db.Execute("UPDATE ifood_order_snapshots SET revision=revision+1");
        Assert.Equal("IFOOD_COMPLETION_STALE_REVISION", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.MarkIfoodCompletionSendingAsync(reservation, options, default))).Code);
        await db.Execute("UPDATE ifood_order_snapshots SET revision=revision-1; UPDATE ifood_delivery_completions SET lease_until_utc=NOW()-INTERVAL '1 second'");
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => repo.MarkIfoodCompletionSendingAsync(reservation, options, default));
        Assert.True(await db.Scalar<bool>("SELECT send_started_at_utc IS NULL FROM ifood_delivery_completions"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_FinalizationRejectsChangedOfficialAmountsAfterPost()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db); await EnqueueOfficial(db, request);
        var client = new CompletionClient { DuringSend = () => db.Execute("UPDATE ifood_order_snapshots SET pending_amount=44,revision=revision+1") };
        var service = CompletionService(db, client); await service.ProcessAsync(default);
        Assert.Equal("review", (await service.GetAsync(7, request.OperationId, default))!.State);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("parcial", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_PassiveMigrationCanRollbackWhenEmptyAndDoesNotEnableOrders()
    {
        await using var db = await RoutingDatabase();
        await db.Migrate("20261008_02_delivery_completion.sql"); await db.Migrate(OfficialCompletionFile); await db.Migrate(OfficialCompletionFile);
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));
        await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261014_03_ifood_delivery_completion.down.sql")));
        Assert.False(await db.Scalar<bool>("SELECT to_regclass('ifood_delivery_completions') IS NOT NULL"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET status_pedido=3 WHERE id=100"));
    }
    [DeliveryDatabaseFact]
    public async Task IfoodCompletion_LateCancellationPreservesReceiptAndCollectionAsHistoricalEvidence()
    {
        await using var db = await OfficialCompletionDatabase(); var request = await OfficialCompletionRequest(db); await EnqueueOfficial(db, request);
        var service = CompletionService(db, new()); await service.ProcessAsync(default); await OfficialTerminal(db, "CAN");
        var operation = (await service.GetAsync(7, request.OperationId, default))!;
        Assert.Equal("review", operation.State); Assert.Equal("IFOOD_COMPLETION_CANCELLED_AFTER_CONFIRMATION", operation.LastErrorCode);
        Assert.NotNull(operation.Receipt); Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_completions"));
        Assert.Equal("pago", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id=100"));
    }
}
