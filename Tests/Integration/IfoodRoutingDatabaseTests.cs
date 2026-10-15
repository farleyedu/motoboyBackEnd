using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string RoutingFile = "20261014_01_ifood_delivery_routing.sql";
    private static async Task<Database> RoutingDatabase()
    {
        var db = await ExceptionDatabase();
        try
        {
            await db.Execute("""
ALTER TABLE pedido ADD COLUMN horario_saida text,ADD COLUMN horario_entrega text;
ALTER TABLE motoboy ADD COLUMN canonical_motoboy_id int;
CREATE TABLE motoboy_estabelecimento(motoboy_id int,estabelecimento_id uuid,ativo bool);
CREATE TABLE motoboy_active_sessions(session_id uuid PRIMARY KEY,motoboy_id int,id_estabelecimento uuid,ended_at_utc timestamptz,expires_at_utc timestamptz,revoked_at timestamptz,paused_at_utc timestamptz);
CREATE TABLE delivery_motoboy_route(motoboy_id int,estabelecimento_id uuid,version bigint DEFAULT 0,updated_at_utc timestamptz,route_state text DEFAULT 'idle',returning_since_utc timestamptz,PRIMARY KEY(motoboy_id,estabelecimento_id));
CREATE TABLE delivery_settings(estabelecimento_id uuid PRIMARY KEY,transfer_policy text DEFAULT 'direct',require_delivery_code bool DEFAULT true,allow_motoboy_reorder bool DEFAULT true,allow_motoboy_refuse bool DEFAULT true,updated_at_utc timestamptz DEFAULT NOW(),store_return_radius_m int DEFAULT 100,require_return_to_store bool DEFAULT false,auto_finish_route_on_return bool DEFAULT true,require_motoboy_acceptance bool DEFAULT true,offer_timeout_minutes int DEFAULT 5);
CREATE TABLE delivery_route_stops(id bigserial PRIMARY KEY,pedido_id int,motoboy_id int,estabelecimento_id uuid,position int,stop_status text,assigned_by_user_id int,assigned_at_utc timestamptz DEFAULT NOW(),picked_up_at_utc timestamptz,arrived_at_utc timestamptz,locked bool DEFAULT false,offer_id uuid,offered_at_utc timestamptz,started_at_utc timestamptz,completed_at_utc timestamptz,completed_by text,updated_at_utc timestamptz,removed_at_utc timestamptz,canceled_at_utc timestamptz,cancel_reason text,transfer_request_id bigint,refused_at_utc timestamptz,refusal_reason text,failed_at_utc timestamptz,failure_reason text);
CREATE UNIQUE INDEX routing_current ON delivery_route_stops(motoboy_id) WHERE stop_status='en_route';
CREATE UNIQUE INDEX routing_position ON delivery_route_stops(motoboy_id,estabelecimento_id,position) WHERE stop_status IN('assigned','en_route');
CREATE TABLE delivery_transfer_requests(id bigint,pedido_id int,estabelecimento_id uuid,from_motoboy_id int,to_motoboy_id int,status text,decision_note text,decided_at_utc timestamptz,updated_at_utc timestamptz);
CREATE TABLE delivery_order_item_checks(stop_id bigint,estabelecimento_id uuid,motoboy_id int,stage text,manifest jsonb,confirmation jsonb,confirmed_at_utc timestamptz DEFAULT NOW(),UNIQUE(stop_id,stage));
INSERT INTO motoboy(id,canonical_motoboy_id) VALUES(1,1);
INSERT INTO motoboy_estabelecimento VALUES(1,@StoreA,true);
INSERT INTO motoboy_active_sessions VALUES(gen_random_uuid(),1,@StoreA,NULL,NOW()+INTERVAL '1 hour',NULL,NULL);
INSERT INTO delivery_settings(estabelecimento_id) VALUES(@StoreA);
""", new { db.StoreA });
            await db.Migrate(RoutingFile);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static Task EnableRouting(Database db) => db.Execute("UPDATE ifood_order_links SET zippy_delivery_enabled=TRUE");
    private static Task<string?> RoutingBlock(Database db, bool depart = false) => db.Scalar<string?>($"SELECT ifood_delivery_blocked_code('{db.StoreA}',100,{(depart ? "TRUE" : "FALSE")})");

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_RoutingDisabledByDefaultAndOnlyConfirmedMerchantDeliveryCanBeOffered()
    {
        await using var db = await RoutingDatabase(); var queue = new PedidoQueueRepository(db.Source);
        Assert.Equal("IFOOD_DELIVERY_DISABLED", await RoutingBlock(db));
        Assert.Equal("IFOOD_DELIVERY_DISABLED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.AssignAsync(db.StoreA, 1, 1, 100))).Code);
        await EnableRouting(db);
        Assert.Equal("IFOOD_DELIVERY_NOT_CONFIRMED", await RoutingBlock(db));
        await Project(db, await ProjectionEvent(db, "CFM"));
        var offer = await queue.AssignAsync(db.StoreA, 1, 1, 100);
        Assert.Null(offer.Current); var order = Assert.IsType<DeliveryStopOrderDto>(Assert.Single(offer.Offer!.Stops).Pedido);
        Assert.Equal("iFood #001234", order.IdentidadeExterna!.DisplayLabel);
        Assert.Equal(1.5m, order.PedidoIfood!.Items[0].Quantity); Assert.Equal(45m, order.PedidoIfood!.AmountToCollect);
        Assert.False(order.RequerCodigoEntrega); Assert.True(order.IdentidadeExterna!.CanAssignDelivery);
        var accepted = await queue.AcceptOfferAsync(db.StoreA, 1);
        Assert.Null(accepted.Current); Assert.Single(accepted.Next);
        Assert.True(await db.Scalar<bool>("SELECT horario_saida IS NULL FROM pedido WHERE id=100"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_RoutingRejectsModesReviewRevocationCancellationAndMissingLocation()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db); await Project(db, await ProjectionEvent(db, "CFM"));
        foreach (var type in new[] { "TAKEOUT", "DINE_IN", "UNKNOWN" })
        {
            await db.Execute("UPDATE ifood_order_snapshots SET order_type=@Type", new { Type = type });
            Assert.Equal("IFOOD_DELIVERY_NOT_APPLICABLE", await RoutingBlock(db));
        }
        await db.Execute("UPDATE ifood_order_snapshots SET order_type='DELIVERY',delivered_by='IFOOD'");
        Assert.Equal("IFOOD_DELIVERY_NOT_APPLICABLE", await RoutingBlock(db));
        await db.Execute("UPDATE ifood_order_snapshots SET delivered_by='MERCHANT',data_is_current=FALSE");
        Assert.Equal("IFOOD_DELIVERY_REVIEW_REQUIRED", await RoutingBlock(db));
        await db.Execute("UPDATE ifood_order_snapshots SET data_is_current=TRUE,cancellation_state='requested'");
        Assert.Equal("IFOOD_DELIVERY_CANCELLATION_PENDING", await RoutingBlock(db));
        await db.Execute("UPDATE ifood_order_snapshots SET cancellation_state='none'; UPDATE pedido SET latitude=NULL WHERE id=100");
        Assert.Equal("IFOOD_DELIVERY_LOCATION_REQUIRED", await RoutingBlock(db));
        await db.Execute("UPDATE ifood_connections SET revoked_at_utc=NOW(),status='revoked'");
        Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED", await RoutingBlock(db));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_DepartureWaitsForOfficialDispatchAndCollectionIsNotInferredFromEvent()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db); await Project(db, await ProjectionEvent(db, "CFM"));
        var queue = new PedidoQueueRepository(db.Source);
        await queue.AssignAsync(db.StoreA, 1, 1, 100); await queue.AcceptOfferAsync(db.StoreA, 1);
        await Project(db, await ProjectionEvent(db, "RTP", 2));
        Assert.Equal("IFOOD_DELIVERY_DISPATCH_PENDING", await RoutingBlock(db, true));
        Assert.Null((await queue.ResumeAsync(db.StoreA, 1, 1)).Current);
        await Project(db, await ProjectionEvent(db, "DSP", 3));
        var started = await queue.ResumeAsync(db.StoreA, 1, 1);
        Assert.Equal(100, started.Current!.PedidoId); Assert.Null(started.Current.PickedUpAtUtc);
        Assert.Equal("IFOOD_ORDER_MANAGED_EXTERNALLY", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.DeliverCurrentAsync(db.StoreA, 1, "0000"))).Code);
        Assert.Equal("IFOOD_ORDER_MANAGED_EXTERNALLY", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.CancelAsync(db.StoreA, 1, 100, "local"))).Code);
        Assert.Equal("IFOOD_PICKUP_CHECKLIST_REQUIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.MarkPickedUpAsync(db.StoreA, 1))).Code);
        var manifest = DeliveryChecklistRules.BuildIfood(started.Current.Pedido!.PedidoIfood!);
        var pickup = new PickupStopsRequest { ExpectedPedidoId = 100, ExpectedVersion = started.Version, PedidoIds = new() { 100 } };
        Assert.Equal("CHECKLIST_CHANGED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.PickUpStopsAsync(db.StoreA, 1, pickup))).Code);
        pickup.Checklists = new() { new() { PedidoId = 100, Version = manifest.Version, ConfirmedKeys = manifest.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() } };
        await queue.PickUpStopsAsync(db.StoreA, 1, pickup);
        Assert.NotNull((await queue.GetQueueAsync(db.StoreA, 1)).Current!.PickedUpAtUtc);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE delivery_route_stops SET stop_status='completed'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET status_pedido=3 WHERE id=100"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_OfficialTerminalEventsRemoveCurrentAndPromoteOwnNextWithoutCreatingReceiptOrPayment()
    {
        foreach (var terminal in new[] { "CAN", "CON" })
        {
            await using var db = await RoutingDatabase(); await EnableRouting(db);
            await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2)); await Project(db, await ProjectionEvent(db, "DSP", 3));
            var queue = new PedidoQueueRepository(db.Source);
            await queue.AssignAsync(db.StoreA, 1, 1, 100); await queue.AcceptOfferAsync(db.StoreA, 1);
            await db.Execute("INSERT INTO pedido(id,id_estabelecimento,status_pedido,motoboy_responsavel)VALUES(101,@StoreA,5,1); INSERT INTO delivery_route_stops(pedido_id,motoboy_id,estabelecimento_id,position,stop_status)VALUES(101,1,@StoreA,2,'assigned')", new { db.StoreA });
            var before = (await queue.GetQueueAsync(db.StoreA, 1)).Version;
            Assert.Equal("applied", await Project(db, await ProjectionEvent(db, terminal, 4)));
            var after = await queue.GetQueueAsync(db.StoreA, 1);
            Assert.Equal(101, after.Current!.PedidoId); Assert.True(after.Version > before);
            Assert.Empty(after.Next); Assert.Equal(terminal == "CAN" ? 4 : 6, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=100"));
            Assert.True(await db.Scalar<bool>("SELECT horario_entrega IS NULL AND status_pagamento='parcial' FROM pedido WHERE id=100"));
        }
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_ScheduledDepartureUsesOfficialWindowAndRemoteHttpAcceptanceIsInsufficient()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db);
        await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2)); await Project(db, await ProjectionEvent(db, "DSP", 3));
        await db.Execute("UPDATE ifood_order_snapshots SET payload=jsonb_set(jsonb_set(payload,'{orderTiming}','\"SCHEDULED\"'),'{schedule}',jsonb_build_object('deliveryDateTimeStart',NOW()+INTERVAL '1 hour','deliveryDateTimeEnd',NOW()+INTERVAL '2 hours'))");
        Assert.Null(await RoutingBlock(db)); Assert.Equal("IFOOD_DELIVERY_BEFORE_WINDOW", await RoutingBlock(db, true));
        await db.Execute("UPDATE ifood_order_snapshots SET payload=jsonb_set(payload,'{schedule}',jsonb_build_object('deliveryDateTimeStart',NOW()-INTERVAL '1 hour','deliveryDateTimeEnd',NOW()+INTERVAL '1 hour'))");
        Assert.Null(await RoutingBlock(db, true));
        await db.Command(Guid.NewGuid(), db.MerchantA, db.StoreA, db.OrderA);
        await db.Execute("UPDATE ifood_outbound_commands SET state='uncertain',http_accepted_at_utc=NOW()");
        Assert.Equal("IFOOD_COMMAND_ACTIVE_OPERATION", await RoutingBlock(db, true));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_MixedBatchRollsBackWhenAnyOrderIsIneligibleAndTenantCannotAssign()
    {
        await using var db = await RoutingDatabase(); var queue = new PedidoQueueRepository(db.Source);
        await db.Execute("INSERT INTO pedido(id,id_estabelecimento,status_pedido)VALUES(101,@StoreA,1)", new { db.StoreA });
        await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.AssignRouteAsync(db.StoreA, 1, 1, new[] { 101, 100 }));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops"));
        Assert.True(await db.Scalar<bool>("SELECT motoboy_responsavel IS NULL FROM pedido WHERE id=101"));
        Assert.Equal(404, (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.AssignAsync(db.StoreB, 1, 1, 100))).StatusCode);
        await db.Migrate(RoutingFile); // reaplicação é segura e não habilita a flag
        Assert.False(await db.Scalar<bool>("SELECT zippy_delivery_enabled FROM ifood_order_links"));
        await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261014_01_ifood_delivery_routing.down.sql")));
        Assert.False(await db.Scalar<bool>("SELECT to_regprocedure('ifood_delivery_blocked_code(uuid,integer,boolean)') IS NOT NULL"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE pedido SET status_pedido=1 WHERE id=100"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_CancellationOutboxFailureRollsBackQueueSnapshotAndCanRecoverSameEvent()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db);
        await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2)); await Project(db, await ProjectionEvent(db, "DSP", 3));
        var queue = new PedidoQueueRepository(db.Source);
        await queue.AssignAsync(db.StoreA, 1, 1, 100); var before = await queue.AcceptOfferAsync(db.StoreA, 1);
        var cancel = await ProjectionEvent(db, "CAN", 4);
        await db.Execute("ALTER TABLE delivery_realtime_outbox ADD CONSTRAINT if12_injected CHECK(payload->>'action' IS DISTINCT FROM 'ifood_canceled')");
        await Assert.ThrowsAsync<APIBack.Integrations.Ifood.IfoodIntegrationException>(() => Project(db, cancel));
        Assert.Equal("DISPATCHED", await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        var rolledBack = await queue.GetQueueAsync(db.StoreA, 1);
        Assert.Equal(before.Version, rolledBack.Version); Assert.Equal(100, rolledBack.Current!.PedidoId);
        await db.Execute("ALTER TABLE delivery_realtime_outbox DROP CONSTRAINT if12_injected");
        Assert.Equal("applied", await Project(db, cancel));
        Assert.Null((await queue.GetQueueAsync(db.StoreA, 1)).Current);
        await Assert.ThrowsAsync<APIBack.Integrations.Ifood.IfoodIntegrationException>(() => Project(db, cancel));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_ConcurrentAssignmentAndCancelNeverLeaveTerminalOrderInActiveQueue()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db);
        await Project(db, await ProjectionEvent(db, "CFM"));
        var cancel = await ProjectionEvent(db, "CAN", 2); var queue = new PedidoQueueRepository(db.Source);
        var assign = Task.Run(async () =>
        {
            try { await queue.AssignAsync(db.StoreA, 1, 1, 100); }
            catch (DeliveryDomainException ex) { Assert.Equal("IFOOD_ORDER_TERMINAL", ex.Code); }
        });
        var project = Task.Run(async () =>
        {
            try { await Project(db, cancel); }
            catch (APIBack.Integrations.Ifood.IfoodIntegrationException ex) when (ex.Code == "IFOOD_DELIVERY_QUEUE_CHANGED") { await Project(db, cancel); }
        });
        await Task.WhenAll(assign, project);
        Assert.Equal("CANCELLED", await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_route_stops WHERE stop_status IN('assigned','en_route')"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_AutoClosureExcludesOfficialOrdersAndAcceptanceRevalidatesReview()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db); await Project(db, await ProjectionEvent(db, "CFM"));
        var queue = new PedidoQueueRepository(db.Source);
        await queue.AssignAsync(db.StoreA, 1, 1, 100);
        Assert.DoesNotContain(await queue.ListOpenOrdersForEncerramentoAsync(db.StoreA), candidate => candidate.Id == 100);
        await db.Execute("UPDATE ifood_order_snapshots SET synchronization_state='review'");
        Assert.Equal("IFOOD_DELIVERY_REVIEW_REQUIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => queue.AcceptOfferAsync(db.StoreA, 1))).Code);
        Assert.Null((await queue.GetQueueAsync(db.StoreA, 1)).Current);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE delivery_route_stops SET stop_status='en_route'"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_CancelDoesNotDispatchAnUnreadyIfoodNextStop()
    {
        await using var db = await RoutingDatabase(); await EnableRouting(db);
        await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2)); await Project(db, await ProjectionEvent(db, "DSP", 3));
        var queue = new PedidoQueueRepository(db.Source);
        await queue.AssignAsync(db.StoreA, 1, 1, 100); await queue.AcceptOfferAsync(db.StoreA, 1);
        var second = Guid.NewGuid();
        await new APIBack.Integrations.Ifood.Orders.IfoodOrderImportRepository(db.Source).ImportAsync(await ImportEvent(db, second),
            APIBack.Integrations.Ifood.Orders.IfoodOrderNormalizer.Normalize(APIBack.Tests.Unit.IfoodOrderImportTests.Payload(db.MerchantA, second), db.MerchantA, second));
        await EnableRouting(db); await Project(db, await ProjectionEvent(db, "CFM", order: second));
        await queue.AssignAsync(db.StoreA, 1, 1, 101); await queue.AcceptOfferAsync(db.StoreA, 1);
        await Project(db, await ProjectionEvent(db, "CAN", 4));
        var after = await queue.GetQueueAsync(db.StoreA, 1);
        Assert.Null(after.Current); Assert.Equal(101, Assert.Single(after.Next).PedidoId);
        Assert.True(await db.Scalar<bool>("SELECT horario_saida IS NULL AND status_pedido=5 FROM pedido WHERE id=101"));
    }

    [DeliveryDatabaseFact]
    public async Task IfoodRouting_DispatchDoesNotDependOnTheLocalRoute()
    {
        // A loja despacha quando quiser: atribuir motoboy Zippy continua opcional e nao condiciona o dispatch.
        await using var db = await RoutingDatabase(); await EnableRouting(db);
        await Project(db, await ProjectionEvent(db, "CFM")); await Project(db, await ProjectionEvent(db, "RTP", 2));
        var commands = CommandRepo(db);
        var request = CommandRequest("dispatch", 3);
        Assert.True((await commands.CapabilitiesAsync(db.StoreA, 100)).Actions.Single(a => a.Command == "dispatch").Available);
        await commands.EnqueueAsync(db.StoreA, 1, 100, request);
        var claimed = await commands.ClaimAsync(); Assert.NotNull(claimed);
        await db.Execute("UPDATE ifood_order_links SET zippy_delivery_enabled=FALSE");
        await commands.MarkSendingAsync(claimed!);
        Assert.True(await db.Scalar<bool>("SELECT send_started_at_utc IS NOT NULL FROM ifood_outbound_commands"));
    }
}
