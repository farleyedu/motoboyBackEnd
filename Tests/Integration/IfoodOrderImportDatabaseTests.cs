using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Dapper;
using Npgsql;
using Moq;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string ImportFile = "20261013_05_ifood_order_import.sql";
    private static async Task<Database> ImportDatabase()
    {
        var db = await IngressDatabase();
        try
        {
            await db.Execute("""
CREATE SEQUENCE pedido_ifood_test_seq START 100;
ALTER TABLE pedido ALTER COLUMN id SET DEFAULT nextval('pedido_ifood_test_seq');
ALTER TABLE pedido ADD COLUMN nome_cliente text, ADD COLUMN endereco_entrega text, ADD COLUMN telefone_cliente text,
ADD COLUMN data_pedido timestamptz,ADD COLUMN horario_pedido time,ADD COLUMN status_pedido int DEFAULT 1,ADD COLUMN motoboy_responsavel int,
ADD COLUMN id_ifood text,ADD COLUMN items text,ADD COLUMN value numeric,ADD COLUMN region text,ADD COLUMN latitude double precision,ADD COLUMN longitude double precision,
ADD COLUMN entrega_rua text,ADD COLUMN entrega_numero text,ADD COLUMN entrega_bairro text,ADD COLUMN entrega_cidade text,ADD COLUMN entrega_estado text,ADD COLUMN entrega_cep text,
ADD COLUMN tipo_pagamento text,ADD COLUMN troco numeric,ADD COLUMN observacoes text,ADD COLUMN status_pagamento text,ADD COLUMN codigo_entrega text,ADD COLUMN previsao_entrega timestamptz;
CREATE TABLE motoboy(id int PRIMARY KEY,nome text);
CREATE TABLE cardapio_produto(id uuid PRIMARY KEY,id_estabelecimento uuid,imagem_url text);
CREATE TABLE delivery_realtime_outbox(event_id uuid PRIMARY KEY,event_name text,target_group text,estabelecimento_id uuid,motoboy_id int,
session_id uuid,session_epoch bigint,aggregate_version bigint,payload jsonb,occurred_at_utc timestamptz);
""");
            await db.Migrate("20260925_02_schema.sql");
            await db.Execute("CREATE UNIQUE INDEX ux_ifood_import_ref_test ON pedido(id_estabelecimento,origem,origem_ref) WHERE origem_ref IS NOT NULL");
            await db.Migrate(ImportFile);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<IfoodInboxReservation> ImportEvent(Database db, Guid order, string code = "PLC")
    {
        var lease = await Lease(db);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { id = Guid.NewGuid(), merchantId = db.MerchantA, orderId = order, code }));
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventEnvelope.Parse(json.RootElement) }, default);
        await db.Inbox.ReleasePollingAsync(lease, TimeSpan.Zero, null, default);
        await db.Execute("UPDATE ifood_polling_state SET next_poll_at_utc=NOW()-INTERVAL '1 second'");
        return Assert.Single(await db.Inbox.ClaimAsync(1, 90, 8, default, new[] { "PLC", "PLACED" }));
    }

    [DeliveryDatabaseFact]
    public async Task ImportPreservesOfficialValuesFractionalItemsAndDoesNotCreateOwnCustomerOrDelivery()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var item = await ImportEvent(db, db.OrderA);
        var order = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA), db.MerchantA, db.OrderA);
        var id = await repo.ImportAsync(item, order);
        Assert.Equal(65m, await db.Scalar<decimal>("SELECT value FROM pedido WHERE id>=100"));
        Assert.Equal(45m, await db.Scalar<decimal>("SELECT pending_amount FROM ifood_order_snapshots"));
        Assert.Equal(1.5m, await db.Scalar<decimal>("SELECT quantity FROM ifood_order_items"));
        Assert.Equal(10.125m, await db.Scalar<decimal>("SELECT unit_price FROM ifood_order_items"));
        Assert.Equal(21m, await db.Scalar<decimal>("SELECT total_price FROM ifood_order_items"));
        Assert.Equal("001234", await db.Scalar<string>("SELECT display_number FROM ifood_order_links"));
        Assert.True(await db.Scalar<bool>("SELECT cliente_id IS NULL AND telefone_cliente IS NULL AND codigo_entrega IS NULL AND id_ifood IS NULL AND status_pedido=6 AND motoboy_responsavel IS NULL FROM pedido WHERE id>=100"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Equal("processed", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        var detail = await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreA, id);
        Assert.Equal("iFood #001234", detail!.IdentidadeExterna!.DisplayLabel);
        Assert.Equal(1.5m, detail.PedidoIfood!.Items[0].Quantity); Assert.Equal(45m, detail.PedidoIfood.AmountToCollect);
        Assert.Empty(detail.Itens); Assert.Null(detail.TelefoneCliente);
        Assert.Null(await new PedidoConsultaRepository(db.Source).GetAsync(db.StoreB, id));
        var list = await new PedidoConsultaRepository(db.Source).ListAsync(db.StoreA, PedidoFiltro.From(null));
        Assert.Equal(65m, list.Itens.Single(p => p.Id == id).Total);
        Assert.Equal("001234", list.Itens.Single(p => p.Id == id).IdentidadeExterna!.DisplayNumber);
    }

    [DeliveryDatabaseFact]
    public async Task TakeoutIfoodLogisticsAndUnknownModesImportWithoutAddressOrRoute()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        foreach (var type in new[] { "TAKEOUT", "DINE_IN", "FUTURE_MODE", "DELIVERY" })
        {
            var orderId = Guid.NewGuid(); var item = await ImportEvent(db, orderId);
            var payload = IfoodOrderImportTests.Payload(db.MerchantA, orderId, type, "IFOOD");
            var order = IfoodOrderNormalizer.Normalize(payload, db.MerchantA, orderId);
            await repo.ImportAsync(item, order);
        }
        Assert.Equal(4, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100 AND status_pedido=6 AND motoboy_responsavel IS NULL"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_snapshots WHERE synchronization_state='review'"));
        Assert.Equal(3, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100 AND endereco_entrega IS NULL"));
    }

    [DeliveryDatabaseFact]
    public async Task DuplicatePlacedEventsHaveOneOrderAndOneOutboxEffectAndConcurrentDetailsFetchHasOneOwner()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var first = await ImportEvent(db, db.OrderA);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => repo.ReserveDetailsAsync(first, 90, default)));
        var owner = Assert.Single(reservations.Where(r => r != null))!;
        var body = IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA);
        await repo.StoreDetailsAsync(owner, body, default);
        var id = await repo.ImportAsync(first, IfoodOrderNormalizer.Normalize(body, db.MerchantA, db.OrderA));
        var second = await ImportEvent(db, db.OrderA);
        var cached = await repo.ReserveDetailsAsync(second, 90, default);
        Assert.Null(cached!.LeaseId); Assert.DoesNotContain("test-only-document", cached.PayloadJson!);
        Assert.Equal(id, await repo.ImportAsync(second, IfoodOrderNormalizer.Normalize(cached.PayloadJson!, db.MerchantA, db.OrderA)));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE state='processed'"));
    }

    [DeliveryDatabaseFact]
    public async Task DatabaseFailureRollsBackOrderLinkItemsOutboxAndEventButKeepsFetchedDetails()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var item = await ImportEvent(db, db.OrderA); var reservation = await repo.ReserveDetailsAsync(item, 90, default);
        var body = IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA);
        await repo.StoreDetailsAsync(reservation!, body, default);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT injected_if07_test_failure CHECK(false)");
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => repo.ImportAsync(item, IfoodOrderNormalizer.Normalize(body, db.MerchantA, db.OrderA)));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links"));
        Assert.Equal("processing", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        Assert.NotNull((await repo.ReserveDetailsAsync(item, 90, default))!.PayloadJson);
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT injected_if07_test_failure");
        await repo.ImportAsync(item, IfoodOrderNormalizer.Normalize(body, db.MerchantA, db.OrderA));
    }

    [DeliveryDatabaseFact]
    public async Task RevocationOrExpiredOwnerCannotCreateOrderAndLegacyMutationsCannotEnableRoute()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var item = await ImportEvent(db, db.OrderA); var order = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA), db.MerchantA, db.OrderA);
        await db.Execute("UPDATE ifood_connections SET status='revoked' WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => repo.ImportAsync(item, order));
        await db.Execute("UPDATE ifood_connections SET status='authorized' WHERE merchant_id=@Merchant", new { Merchant = db.MerchantA });
        await db.Execute("UPDATE ifood_events_inbox SET lease_until_utc=NOW()-INTERVAL '1 second'");
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => repo.ImportAsync(item, order));
        var recovered = Assert.Single(await db.Inbox.ClaimAsync(1, 90, 8));
        var id = await repo.ImportAsync(recovered, order);
        var queue = new PedidoQueueRepository(db.Source);
        Assert.Equal("IFOOD_ORDER_MANAGED_EXTERNALLY", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.ConfirmPedidoAsync(db.StoreA, 1, id, 30))).Code);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET status_pedido=1 WHERE id=@Id", new { Id = id }));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET motoboy_responsavel=1 WHERE id=@Id", new { Id = id }));
    }

    [DeliveryDatabaseFact]
    public async Task ImportWorkerReadsOnlyPlacedAndReusesCachedDetailsAcrossRetry()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var item = await ImportEvent(db, db.OrderA); var body = IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA);
        var reservation = await repo.ReserveDetailsAsync(item, 90, default); await repo.StoreDetailsAsync(reservation!, body, default);
        await db.Inbox.RetryEventAsync(item, 8, TimeSpan.Zero);
        await db.Execute("INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,external_order_id,event_code,payload) VALUES(@Merchant,@Store,@Event,@Order,'CFM','{}')",
            new { Merchant = db.MerchantA, Store = db.StoreA, Event = Guid.NewGuid(), Order = db.OrderA });
        var options = IfoodAuthenticationTests.EnabledOptions(); options.ImportEnabled = true;
        var details = new Mock<IIfoodOrderDetailsClient>(MockBehavior.Strict);
        var service = new IfoodOrderImportService(db.Inbox, repo, details.Object, Microsoft.Extensions.Options.Options.Create(options));
        Assert.Equal(1, await service.RunOnceAsync());
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE event_code='CFM' AND state='queued'"));
    }

    [DeliveryDatabaseFact]
    public async Task ImportMigrationIsRepeatableAndRollbackPreservesCacheAndRequiresDependencyOrder()
    {
        await using var db = await ImportDatabase(); await db.Migrate(ImportFile);
        var ingressDown = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261013_04_ifood_event_ingress.down.sql"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(ingressDown));
        var down = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261013_05_ifood_order_import.down.sql"));
        await db.Execute(down); await db.Migrate(ImportFile);
        var item = await ImportEvent(db, db.OrderA); await new IfoodOrderImportRepository(db.Source).ReserveDetailsAsync(item, 90, default);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(down));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido"));
    }

    [DeliveryDatabaseFact]
    public async Task CompleteDetailsHttpImportIsRecoverableAndDoesNotRefetchDuplicateOrder()
    {
        await using var db = await ImportDatabase(); var repo = new IfoodOrderImportRepository(db.Source);
        var item = await ImportEvent(db, db.OrderA); await db.Inbox.RetryEventAsync(item, 8, TimeSpan.Zero);
        var options = IfoodAuthenticationTests.EnabledOptions(); options.ImportEnabled = true;
        var tokens = new Mock<IIfoodTokenProvider>();
        tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("test-only-token", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        var access = new IfoodMerchantAccess(db.Connections, tokens.Object, Microsoft.Extensions.Options.Options.Create(options));
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (request, cancellation) =>
            {
                Assert.Equal($"https://merchant-api.ifood.com.br/order/v1.0/orders/{db.OrderA:D}", request.RequestUri!.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA)) });
            }
        };
        var client = new IfoodOrderDetailsClient(new IfoodOrderImportTests.Factory(handler), access, tokens.Object);
        IfoodOrderImportService NewProcess() => new(db.Inbox, repo, client, Microsoft.Extensions.Options.Options.Create(options));
        Assert.Equal(1, await NewProcess().RunOnceAsync());
        var next = await ImportEvent(db, db.OrderA); await db.Inbox.RetryEventAsync(next, 8, TimeSpan.Zero);
        Assert.Equal(1, await NewProcess().RunOnceAsync());
        Assert.Equal(1, handler.Calls);
        Assert.False(await db.Scalar<bool>("SELECT payload::text LIKE '%test-only-document%' OR payload::text LIKE '%private-pickup-code%' FROM ifood_order_details_cache"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100"));
    }

    [DeliveryDatabaseFact]
    public async Task ImportSupportsShortLegacyDateAndTimeFieldsWithoutChangingExternalTimestamp()
    {
        await using var db = await ImportDatabase();
        await db.Execute("ALTER TABLE pedido ALTER COLUMN data_pedido TYPE varchar(10) USING NULL; ALTER TABLE pedido ALTER COLUMN horario_pedido TYPE varchar(5) USING NULL");
        var item = await ImportEvent(db, db.OrderA);
        await new IfoodOrderImportRepository(db.Source).ImportAsync(item,
            IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA, db.OrderA), db.MerchantA, db.OrderA));
        Assert.Equal("2026-10-13", await db.Scalar<string>("SELECT data_pedido FROM pedido WHERE id>=100"));
        Assert.Equal("15:00", await db.Scalar<string>("SELECT horario_pedido FROM pedido WHERE id>=100"));
        Assert.Equal("2026-10-13T18:00:00Z", await db.Scalar<string>("SELECT payload->>'createdAt' FROM ifood_order_snapshots"));
    }
}
