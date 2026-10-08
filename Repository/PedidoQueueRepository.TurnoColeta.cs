using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    public async Task<MotoboyQueueDto> PickUpStopsAsync(Guid estabelecimentoId, int motoboyId, PickupStopsRequest request)
    {
        DeliveryPickupRules.ValidateRequest(request);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var version = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
        var current = await GetCurrentStopAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true)
            ?? throw new DeliveryDomainException(409, "NO_CURRENT_DELIVERY", "Não há entrega atual para coletar.");
        DeliveryArrivalRules.EnsureExpectedPedido(request.ExpectedPedidoId, current.PedidoId);
        var stops = await GetActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true);
        var before = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        var acceptedIds = before.Next.Select(s => s.PedidoId).Append(current.PedidoId).ToArray();
        var alreadyPickedUp = stops.Where(s => request.PedidoIds.Contains(s.PedidoId)).All(s => s.PickedUpAtUtc.HasValue);
        DeliveryPickupRules.ValidateSnapshot(request, version, acceptedIds, alreadyPickedUp);
        if (!alreadyPickedUp)
        {
            await connection.ExecuteAsync("""
                UPDATE delivery_route_stops SET picked_up_at_utc = COALESCE(picked_up_at_utc, NOW()), updated_at_utc = NOW()
                 WHERE estabelecimento_id = @Est AND motoboy_id = @Motoboy AND pedido_id = ANY(@Ids)
                   AND stop_status IN ('assigned', 'en_route');
                """, new { Est = estabelecimentoId, Motoboy = motoboyId, Ids = request.PedidoIds.ToArray() }, transaction);
            version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, current.PedidoId, "picked_up", version, await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId));
        }
        var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        await transaction.CommitAsync(); return snapshot;
    }

    public async Task<MotoboyQueueDto> PauseTurnAsync(Guid estabelecimentoId, int motoboyId, Guid sessionId, long sessionEpoch, bool paused)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var version = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
        var before = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        if (paused && before.Offer != null) throw new DeliveryDomainException(409, "OFFER_PENDING", "Responda a oferta pendente antes de pausar novos chamados.");
        var updated = await connection.ExecuteAsync("""
            UPDATE motoboy_active_sessions SET paused_at_utc = CASE WHEN @Paused THEN COALESCE(paused_at_utc, NOW()) ELSE NULL END
             WHERE session_id = @SessionId AND session_epoch = @Epoch AND id_estabelecimento = @Est
               AND motoboy_id = (SELECT canonical_motoboy_id FROM motoboy WHERE id = @Motoboy)
               AND ended_at_utc IS NULL AND revoked_at IS NULL AND expires_at_utc > NOW();
            """, new { Paused = paused, SessionId = sessionId, Epoch = sessionEpoch, Est = estabelecimentoId, Motoboy = motoboyId }, transaction);
        if (updated == 0) throw new DeliveryDomainException(409, "SESSION_CHANGED", "Seu turno mudou. Recupere a sessão.");
        if (before.Paused != paused)
        {
            version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, paused ? "paused" : "dispatch_resumed", version, sessionId);
        }
        var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        await transaction.CommitAsync(); return snapshot;
    }
}
