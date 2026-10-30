using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private static Task<bool> HasIfoodRoutingAsync(NpgsqlConnection c, NpgsqlTransaction? tx) =>
        c.ExecuteScalarAsync<bool>("SELECT to_regprocedure('ifood_delivery_blocked_code(uuid,integer,boolean)') IS NOT NULL", transaction: tx);

    private static async Task<string?> IfoodRoutingBlockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, bool departure)
    {
        if (!await HasIfoodRoutingAsync(c, tx)) return null;
        return await c.ExecuteScalarAsync<string?>("SELECT ifood_delivery_blocked_code(@Store,@Pedido,@Departure)",
            new { Store = store, Pedido = pedido, Departure = departure }, tx);
    }

    private static async Task EnsureIfoodRoutingAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, bool departure)
    {
        var blocked = await IfoodRoutingBlockAsync(c, tx, store, pedido, departure);
        if (blocked != null) throw new DeliveryDomainException(409, blocked,
            "A logística deste pedido iFood está bloqueada. Confira o preparo, despacho e sincronização na operação iFood.");
    }

    // O projetor trava a fila ANTES de snapshot/pedido. Revalida ownership para não tocar uma fila descoberta por leitura antiga.
    internal static async Task LockIfoodQueueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, Guid merchant, Guid order)
    {
        if (!await HasIfoodRoutingAsync(c, tx)) return;
        var owner = await c.QuerySingleOrDefaultAsync<(int Pedido, int? Rider)>("""
SELECT p.id AS Pedido,p.motoboy_responsavel AS Rider FROM pedido p JOIN ifood_order_links l ON l.pedido_id=p.id AND l.estabelecimento_id=p.id_estabelecimento
WHERE l.estabelecimento_id=@Store AND l.merchant_id=@Merchant AND l.external_order_id=@Order
""", new { Store = store, Merchant = merchant, Order = order }, tx);
        if (owner.Pedido == 0) return;
        if (owner.Rider.HasValue) await LockQueuesAsync(c, tx, store, owner.Rider.Value);
        var actual = await c.QuerySingleAsync<PedidoRow>("SELECT id AS Id,motoboy_responsavel AS MotoboyResponsavel FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store FOR UPDATE",
            new { owner.Pedido, Store = store }, tx);
        if (actual.MotoboyResponsavel != owner.Rider)
            throw new APIBack.Integrations.Ifood.IfoodIntegrationException("IFOOD_DELIVERY_QUEUE_CHANGED", retryable: true);
    }

    internal static async Task ReconcileIfoodQueueAsync(APIBack.Acontecimentos.IAcontecimentosService livro, NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, string remote)
    {
        if (!await HasIfoodRoutingAsync(c, tx)) return;
        if (await ReconcileOfficialCompletion(livro, c, tx, store, pedido, remote)) return;
        var stop = await GetActiveStopForPedidoAsync(c, tx, store, pedido, true);
        if (stop == null) return;
        if (remote is "CANCELLED" or "CONCLUDED")
        {
            // CON arquiva a parada sem recibo, pagamento, código validado ou remuneração de entrega local.
            await c.ExecuteAsync(remote == "CANCELLED"
                ? "UPDATE delivery_route_stops SET stop_status='canceled',canceled_at_utc=NOW(),cancel_reason='Cancelamento oficial iFood',updated_at_utc=NOW() WHERE id=@Id"
                : "UPDATE delivery_route_stops SET stop_status='removed',removed_at_utc=NOW(),updated_at_utc=NOW() WHERE id=@Id", new { stop.Id }, tx);
            await CancelPendingTransfersForPedidoAsync(c, tx, store, pedido, "Pedido encerrado oficialmente no iFood.");
            if ((await GetEligibilityAsync(c, tx, stop.MotoboyId, store)).SessionId.HasValue)
                await TryPromoteNextAsync(c, tx, store, stop.MotoboyId);
            await RenumberActiveStopsAsync(c, tx, store, stop.MotoboyId);
            await EnterReturningIfIdleAsync(c, tx, store, stop.MotoboyId);
        }
        // DSP não comprova coleta: somente o comando de coleta grava picked_up_at_utc.
        var version = await BumpRouteVersionAsync(c, tx, store, stop.MotoboyId);
        await EmitQueueEventAsync(c, tx, store, stop.MotoboyId, pedido,
            remote == "CANCELLED" ? "ifood_canceled" : remote == "CONCLUDED" ? "ifood_closed" : "ifood_updated", version,
            await GetSessionIdAsync(c, tx, stop.MotoboyId, store));
    }
}
