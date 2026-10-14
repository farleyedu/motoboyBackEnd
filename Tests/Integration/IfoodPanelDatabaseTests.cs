using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Commands;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Tests.Unit;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task PanelCapabilitiesAreReadOnlyScopedToTenantAndExposeDisabledAndRevokedActions()
    {
        await using var db = await ExceptionDatabase(); var rig = Rig(db);
        var initial = await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox");
        var dto = await rig.Repo.CapabilitiesAsync(db.StoreA, 100);
        Assert.Equal(1, dto.Revision); Assert.Empty(dto.Operations);
        Assert.True(dto.Actions.Single(a => a.Command == "request_cancellation").Available);
        Assert.False(dto.Actions.Single(a => a.Command == "confirm").Available);
        Assert.Empty(rig.Calls);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_outbound_commands"));
        Assert.Equal(initial, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => rig.Repo.CapabilitiesAsync(db.StoreB, 100));
        Assert.Equal(404, error.StatusCode);
        await db.Execute("UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW()");
        Assert.All((await rig.Repo.CapabilitiesAsync(db.StoreA, 100)).Actions, a => Assert.Equal("IFOOD_MERCHANT_NOT_AUTHORIZED", a.BlockedCode));
    }

    [DeliveryDatabaseFact]
    public async Task PanelNeverOffersSecondIntentionWhileAnyOrderOperationIsActive()
    {
        await using var db = await ExceptionDatabase(); var repo = CommandRepo(db);
        var operation = await repo.EnqueueAsync(db.StoreA, 1, 100, CommandRequest());
        foreach (var state in new[] { "queued", "processing", "uncertain", "review" })
        {
            await db.Execute("""
UPDATE ifood_outbound_commands SET state=@State,
lease_id=CASE WHEN @State='processing' THEN gen_random_uuid() ELSE NULL END,
lease_until_utc=CASE WHEN @State='processing' THEN NOW()+INTERVAL '1 minute' ELSE NULL END
""", new { State = state });
            var dto = await repo.CapabilitiesAsync(db.StoreA, 100);
            Assert.Equal(operation.OperationId, Assert.Single(dto.Operations).OperationId);
            Assert.Equal(state, dto.Operations[0].State);
            Assert.All(dto.Actions, a => Assert.Equal("IFOOD_COMMAND_ACTIVE_OPERATION", a.BlockedCode));
        }
        Assert.Equal("PLACED", await db.Scalar<string>("SELECT remote_status FROM ifood_order_snapshots"));
    }

    [DeliveryDatabaseFact]
    public async Task PanelSearchFindsOfficialNumberAndUuidWithCorrectPaginationAndStoreIsolation()
    {
        await using var db = await ExceptionDatabase(); var reader = new PedidoConsultaRepository(db.Source);
        foreach (var search in new[] { "001234", "#001234", db.OrderA.ToString("D") })
        {
            var found = await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Busca = search, PageSize = 1 }));
            Assert.Equal(1, found.Total); Assert.Equal(100, Assert.Single(found.Itens).Id);
            Assert.Equal("001234", found.Itens[0].IdentidadeExterna!.DisplayNumber);
            Assert.Empty((await reader.ListAsync(db.StoreB, PedidoFiltro.From(new() { Busca = search }))).Itens);
        }
        Assert.Empty((await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Modalidade = "TAKEOUT" }))).Itens);
        Assert.Equal(100, Assert.Single((await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Modalidade = "DELIVERY", Preparo = "awaiting_acceptance" }))).Itens).Id);
        Assert.Empty((await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Preparo = "ready" }))).Itens);
        Assert.Empty((await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Busca = "%" }))).Itens);
    }

    [DeliveryDatabaseFact]
    public async Task PanelKitchenListsTakeoutAndUnknownModesWithoutDeliveryAddress()
    {
        await using var db = await ExceptionDatabase(false);
        foreach (var mode in new[] { "TAKEOUT", "DINE_IN", "FUTURE_MODE" })
        {
            var order = Guid.NewGuid(); var item = await ImportEvent(db, order);
            await new IfoodOrderImportRepository(db.Source).ImportAsync(item,
                IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(db.MerchantA, order, mode, "IFOOD"), db.MerchantA, order));
        }
        var reader = new PedidoConsultaRepository(db.Source);
        foreach (var mode in new[] { "TAKEOUT", "DINE_IN", "UNKNOWN" })
        {
            var list = await reader.ListAsync(db.StoreA, PedidoFiltro.From(new() { Modalidade = mode }));
            Assert.Equal(1, list.Total); Assert.Null(Assert.Single(list.Itens).EnderecoEntrega);
        }
        Assert.Equal(3, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE id>=100 AND motoboy_responsavel IS NULL"));
    }
}
