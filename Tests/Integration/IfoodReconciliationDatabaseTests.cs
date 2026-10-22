using System.Text.Json;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Integrations.Ifood.Reconciliation;
using APIBack.Integrations.Ifood.Retention;
using APIBack.Tests.Unit;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string ReconciliationFile = "20261015_01_ifood_reconciliation_retention.sql";
    private const string ReconciliationRollbackFile = "20261015_01_ifood_reconciliation_retention.down.sql";

    private static async Task<Database> ReconciliationDatabase()
    {
        var db = await OfficialCompletionDatabase();
        try
        {
            await db.Migrate(RuntimeSettingsFile);
            await db.Migrate(RuntimeFlagsFile);
            await db.Migrate(ReconciliationFile);
            await db.Migrate(LifecycleFile);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    // Evento cujo código curto contradiz o fullCode: a projeção o coloca em revisão (IFOOD_EVENT_CODE_CONFLICT).
    private static async Task<IfoodInboxReservation> ConflictingEvent(Database db, int minute)
    {
        var id = Guid.NewGuid(); var at = DateTimeOffset.Parse("2026-10-13T18:00:00Z").AddMinutes(minute);
        var body = JsonSerializer.Serialize(new { id, merchantId = db.MerchantA, orderId = db.OrderA, code = "DDCR", fullCode = "OUTRO_EVENTO", createdAt = at, metadata = new { } });
        await db.Execute("""
INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,external_order_id,event_code,payload,occurred_at_utc)
VALUES(@Merchant,@Store,@Event,@Order,'DDCR',CAST(@Payload AS jsonb),@At)
""", new { Merchant = db.MerchantA, Store = db.StoreA, Event = id, Order = db.OrderA, Payload = body, At = at });
        return Assert.Single(await db.Inbox.ClaimAsync(1, 90, 8, default, new[] { "DDCR" }));
    }

    [DeliveryDatabaseFact]
    public async Task ReconciliationListsOnlyTheStoresEventsWithoutPayloadAndAuditsEveryManualAction()
    {
        await using var db = await ReconciliationDatabase();
        var repository = new IfoodReconciliationRepository(db.Source);
        var events = await repository.ListOrderEventsAsync(db.StoreA, 100);
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.EventCode == "CFM" && e.Outcome == "applied");
        Assert.Empty(await repository.ListOrderEventsAsync(db.StoreB, 100));
        Assert.Empty(await repository.ListOrderEventsAsync(db.StoreA, 999));

        var conflict = await ConflictingEvent(db, 10);
        Assert.Equal("review", await Project(db, conflict));
        Assert.Equal("review", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        var reviewRow = Assert.Single(await repository.ListOrderEventsAsync(db.StoreA, 100), e => e.State == "review");
        Assert.Equal("IFOOD_EVENT_CODE_CONFLICT", reviewRow.ReasonCode);

        // Com evento ainda em revisão, o pedido não é liberado.
        var pending = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.ResolveReviewAsync(db.StoreA, 100, 7, "conferido no portal"));
        Assert.Equal("IFOOD_RECONCILIATION_EVENTS_PENDING", pending.Code);

        // Anotação obrigatória; outra loja não alcança o evento.
        var note = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.DismissEventAsync(db.StoreA, 100, reviewRow.InboxId, 7, "ok"));
        Assert.Equal("IFOOD_RECONCILIATION_NOTE_REQUIRED", note.Code);
        var other = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.DismissEventAsync(db.StoreB, 100, reviewRow.InboxId, 7, "conferido no portal"));
        Assert.Equal("IFOOD_RECONCILIATION_NOT_FOUND", other.Code);

        await repository.DismissEventAsync(db.StoreA, 100, reviewRow.InboxId, 7, "evento informativo conferido no portal");
        Assert.Equal("ignored", await db.Scalar<string>("SELECT state FROM ifood_events_inbox WHERE id=@Id", new { Id = reviewRow.InboxId }));
        // O histórico continua mostrando o que aconteceu; só o estado do evento mudou.
        Assert.Equal("review", await db.Scalar<string>("SELECT outcome FROM ifood_order_event_history WHERE inbox_id=@Id", new { Id = reviewRow.InboxId }));

        var revisionBefore = await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots");
        await repository.ResolveReviewAsync(db.StoreA, 100, 7, "conferido no portal do iFood");
        Assert.Equal("synced", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
        Assert.Equal(revisionBefore + 1, await db.Scalar<long>("SELECT revision FROM ifood_order_snapshots"));
        var again = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.ResolveReviewAsync(db.StoreA, 100, 7, "conferido de novo"));
        Assert.Equal("IFOOD_RECONCILIATION_INVALID_STATE", again.Code);

        Assert.Equal(new[] { "dismiss", "resolve_review" },
            (await db.Query<string>("SELECT action FROM ifood_reconciliation_audit ORDER BY id")).ToArray());
        Assert.Equal(7, await db.Scalar<int>("SELECT actor_user_id FROM ifood_reconciliation_audit WHERE action='dismiss'"));
        Assert.Equal("review", await db.Scalar<string>("SELECT previous_state FROM ifood_reconciliation_audit WHERE action='resolve_review'"));
    }

    [DeliveryDatabaseFact]
    public async Task RequeueRepeatsTheProjectionWithoutAlteringTheOrderAndRefusesExpiredPayloads()
    {
        await using var db = await ReconciliationDatabase();
        var repository = new IfoodReconciliationRepository(db.Source);
        var conflict = await ConflictingEvent(db, 11);
        Assert.Equal("review", await Project(db, conflict));
        var row = Assert.Single(await repository.ListOrderEventsAsync(db.StoreA, 100), e => e.State == "review");

        await repository.RequeueEventAsync(db.StoreA, 100, row.InboxId, 7);
        Assert.Equal("queued", await db.Scalar<string>("SELECT state FROM ifood_events_inbox WHERE id=@Id", new { Id = row.InboxId }));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_event_history WHERE inbox_id=@Id", new { Id = row.InboxId }));
        Assert.Null(await db.Scalar<string?>("SELECT last_error_code FROM ifood_events_inbox WHERE id=@Id", new { Id = row.InboxId }));
        Assert.Equal("requeue", await db.Scalar<string>("SELECT action FROM ifood_reconciliation_audit ORDER BY id DESC LIMIT 1"));

        // Evento que não está em revisão/falha não pode ser reenfileirado.
        var applied = Assert.Single(await repository.ListOrderEventsAsync(db.StoreA, 100), e => e.EventCode == "DSP");
        var invalid = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.RequeueEventAsync(db.StoreA, 100, applied.InboxId, 7));
        Assert.Equal("IFOOD_RECONCILIATION_INVALID_STATE", invalid.Code);

        // Depois da retenção o corpo foi apagado: não há o que reprocessar.
        await db.Execute("UPDATE ifood_events_inbox SET state='review', payload='{}'::jsonb WHERE id=@Id", new { Id = row.InboxId });
        var expired = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.RequeueEventAsync(db.StoreA, 100, row.InboxId, 7));
        Assert.Equal("IFOOD_RECONCILIATION_PAYLOAD_EXPIRED", expired.Code);
    }

    [DeliveryDatabaseFact]
    public async Task ResolveRefusesWhileAnUncertainCommandOrStalePatchExists()
    {
        await using var db = await ReconciliationDatabase();
        var repository = new IfoodReconciliationRepository(db.Source);
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review', data_is_current=FALSE");
        var stale = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.ResolveReviewAsync(db.StoreA, 100, 7, "conferido no portal"));
        Assert.Equal("IFOOD_RECONCILIATION_DATA_STALE", stale.Code);
        await db.Execute("UPDATE ifood_order_snapshots SET data_is_current=TRUE");
        await db.Execute("""
INSERT INTO ifood_outbound_commands(operation_id,merchant_id,external_order_id,estabelecimento_id,command_type,payload,payload_hash,expected_order_revision,state)
VALUES(@Op,@Merchant,@Order,@Store,'dispatch','{}'::jsonb,repeat('a',64),1,'uncertain')
""", new { Op = Guid.NewGuid(), Merchant = db.MerchantA, Order = db.OrderA, Store = db.StoreA });
        var command = await Assert.ThrowsAsync<IfoodIntegrationException>(() => repository.ResolveReviewAsync(db.StoreA, 100, 7, "conferido no portal"));
        Assert.Equal("IFOOD_RECONCILIATION_COMMAND_PENDING", command.Code);
        Assert.Equal("review", await db.Scalar<string>("SELECT synchronization_state FROM ifood_order_snapshots"));
    }

    [DeliveryDatabaseFact]
    public async Task AlertsCoverAcceptanceDeadlineReviewFailuresAndStayWithinTheStore()
    {
        await using var db = await ReconciliationDatabase();
        var repository = new IfoodReconciliationRepository(db.Source);
        Assert.Empty(await repository.GetStoreAlertsAsync(db.StoreA));

        // Aguardando aceite há muito mais de 6 minutos (o payload de teste é de 13/10).
        await db.Execute("UPDATE ifood_order_snapshots SET remote_status='PLACED', preparation_state='awaiting_acceptance'");
        var alerts = await repository.GetStoreAlertsAsync(db.StoreA);
        var deadline = Assert.Single(alerts, a => a.Code == "IFOOD_ACCEPTANCE_DEADLINE");
        Assert.Equal("critical", deadline.Severity); Assert.Equal(1, deadline.Count);
        Assert.Empty(await repository.GetStoreAlertsAsync(db.StoreB));

        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review'");
        await db.Execute("UPDATE ifood_events_inbox SET state='failed' WHERE id=(SELECT MIN(id) FROM ifood_events_inbox)");
        alerts = await repository.GetStoreAlertsAsync(db.StoreA);
        Assert.Contains(alerts, a => a.Code == "IFOOD_ORDER_REVIEW" && a.Severity == "warning" && a.Count == 1);
        Assert.Contains(alerts, a => a.Code == "IFOOD_EVENT_FAILED" && a.Severity == "critical");

        await db.Execute("UPDATE ifood_events_inbox SET state='queued', received_at_utc=NOW()-INTERVAL '10 minutes' WHERE id=(SELECT MAX(id) FROM ifood_events_inbox)");
        Assert.Contains(await repository.GetStoreAlertsAsync(db.StoreA), a => a.Code == "IFOOD_EVENT_BACKLOG");
    }

    [DeliveryDatabaseFact]
    public async Task RetentionClearsOnlyOldProcessedPayloadsAndOnlyWhenTurnedOnWithAValidWindow()
    {
        await using var db = await ReconciliationDatabase();
        await db.Execute("UPDATE ifood_events_inbox SET state='processed', acknowledged_at_utc=NOW(), received_at_utc=NOW()-INTERVAL '40 days' WHERE id IN (SELECT id FROM ifood_events_inbox ORDER BY id LIMIT 2)");
        var conflict = await ConflictingEvent(db, 12);
        await Project(db, conflict);
        await db.Execute("UPDATE ifood_events_inbox SET received_at_utc=NOW()-INTERVAL '40 days' WHERE state='review'");
        var total = await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox");

        IfoodRetentionService Service(bool on, int days)
        {
            var options = IfoodCommandTests.Options(db.MerchantA, db.StoreA); options.Enabled = true; options.RetentionEnabled = on; options.RetentionDays = days;
            return new IfoodRetentionService(db.Source, IfoodAuthenticationTests.Monitor(options));
        }
        Assert.Equal(0, (await Service(false, 30).RunOnceAsync()).InboxPayloadsCleared);
        Assert.Equal(0, (await Service(true, 3).RunOnceAsync()).InboxPayloadsCleared);       // janela abaixo do mínimo
        Assert.Equal(0, (await Service(true, 400).RunOnceAsync()).InboxPayloadsCleared);     // acima do máximo

        var result = await Service(true, 30).RunOnceAsync();
        Assert.Equal(2, result.InboxPayloadsCleared);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE payload='{}'::jsonb"));
        // Evento em revisão e eventos recentes nunca são limpos; nada é apagado.
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE state='review' AND payload<>'{}'::jsonb"));
        Assert.Equal(total, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        Assert.Equal(0, (await Service(true, 30).RunOnceAsync()).InboxPayloadsCleared);       // idempotente
    }

    [DeliveryDatabaseFact]
    public async Task NewOrdersInheritTheStoresZippyDeliveryFlagAndApplyToOpenOrdersSkipsClosedOnes()
    {
        await using var db = await ProjectionDatabase(import: false);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        var settings = new IfoodRuntimeSettingsRepository(db.Source);
        await ImportOrder(db, db.OrderA, db.MerchantA);
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));   // padrão: desligado

        // Liga a loja e aplica aos pedidos abertos: o pedido de entrega da loja muda.
        var updated = await settings.SetConnectionDeliveryAsync(db.MerchantA, true, applyToOpenOrders: true);
        Assert.Equal(1, updated);
        Assert.True(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));
        // Reaplicar não muda nada; desligar com "aplicar" volta o pedido aberto.
        Assert.Equal(0, await settings.SetConnectionDeliveryAsync(db.MerchantA, true, applyToOpenOrders: true));
        Assert.Equal(1, await settings.SetConnectionDeliveryAsync(db.MerchantA, false, applyToOpenOrders: true));
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));

        // Pedido encerrado no iFood nunca é tocado.
        await db.Execute("UPDATE ifood_order_snapshots SET remote_status='CONCLUDED'");
        Assert.Equal(0, await settings.SetConnectionDeliveryAsync(db.MerchantA, true, applyToOpenOrders: true));
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));

        // Sem "aplicar", só a conexão muda e os pedidos NOVOS herdam o valor; o encerrado continua desligado.
        Assert.Equal(0, await settings.SetConnectionDeliveryAsync(db.MerchantA, true));
        Assert.True((await settings.GetStoreConnectionAsync(db.StoreA))!.ZippyDeliveryEnabled);
        var second = Guid.NewGuid();
        await ImportOrder(db, second, db.MerchantA);
        Assert.True(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links WHERE external_order_id=@Order", new { Order = second }));
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links WHERE external_order_id=@Order", new { Order = db.OrderA }));
    }

    private static async Task ImportOrder(Database db, Guid order, Guid merchant)
    {
        var item = await ImportEvent(db, order);
        await new IfoodOrderImportRepository(db.Source).ImportAsync(item, IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(merchant, order), merchant, order));
    }

    [DeliveryDatabaseFact]
    public async Task ReconciliationMigrationIsIdempotentAndItsRollbackKeepsTheAuditTrail()
    {
        await using var db = await ReconciliationDatabase();
        await db.Migrate(ReconciliationFile);
        await db.Migrate(LifecycleFile);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_app_settings WHERE retention_days=30 AND NOT retention_enabled"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Execute("UPDATE ifood_app_settings SET retention_days=3"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Execute("UPDATE ifood_app_settings SET retention_days=500"));

        var repository = new IfoodReconciliationRepository(db.Source);
        var conflict = await ConflictingEvent(db, 13);
        await Project(db, conflict);
        var row = Assert.Single(await repository.ListOrderEventsAsync(db.StoreA, 100), e => e.State == "review");
        await repository.DismissEventAsync(db.StoreA, 100, row.InboxId, 7, "evento conferido no portal");
        var rollback = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", ReconciliationRollbackFile));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Execute(rollback));               // auditoria existente impede o rollback
        await db.Execute("DELETE FROM ifood_reconciliation_audit");
        await db.Execute("UPDATE ifood_app_settings SET retention_enabled=TRUE");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Execute(rollback));               // limpeza ligada também
        await db.Execute("UPDATE ifood_app_settings SET retention_enabled=FALSE");
        await db.Execute(rollback);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.tables WHERE table_name='ifood_reconciliation_audit' AND table_schema=current_schema()"));
    }
}
