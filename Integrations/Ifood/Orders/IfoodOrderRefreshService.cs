using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Hubs;
using APIBack.Integrations.Ifood.Events;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

// Reconsulta dados oficiais sem inventar estado de ciclo de vida ou dispensar eventos.
public sealed class IfoodOrderRefreshService(NpgsqlDataSource source, IfoodOrderImportRepository repository,
    IIfoodOrderDetailsClient details, IOptionsMonitor<IfoodOptions> options, IServiceScopeFactory? scopes = null)
{
    private sealed class Context
    {
        public Guid MerchantId { get; set; }
        public Guid ExternalOrderId { get; set; }
        public long Revision { get; set; }
        public string Payload { get; set; } = "{}";
        public string SynchronizationState { get; set; } = "";
    }

    public async Task RefreshAsync(Guid store, int user, int pedido, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled || !options.CurrentValue.ImportEnabled) throw new IfoodIntegrationException("IFOOD_DISABLED");
        await using var connection = await source.OpenConnectionAsync(ct);
        var context = await connection.QuerySingleOrDefaultAsync<Context>(new CommandDefinition("""
SELECT l.merchant_id AS MerchantId,l.external_order_id AS ExternalOrderId,s.revision AS Revision,s.payload::text AS Payload,s.synchronization_state AS SynchronizationState
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido AND c.status='authorized'
AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
""", new { Store = store, Pedido = pedido }, cancellationToken: ct))
            ?? throw new DeliveryDomainException(404, "IFOOD_ORDER_NOT_FOUND", "Pedido não encontrado ou conexão revogada.");
        var item = new IfoodInboxReservation { MerchantId = context.MerchantId, ExternalOrderId = context.ExternalOrderId, EstabelecimentoId = store };
        var reservation = await repository.ReserveDetailsAsync(item, 90, ct, force: true)
            ?? throw new IfoodIntegrationException("IFOOD_DETAILS_BUSY", retryable: true);
        var payload = reservation.PayloadJson;
        try
        {
            if (payload == null)
            {
                payload = await details.GetAsync(reservation, token => repository.RenewDetailsAsync(reservation, 90, token), ct);
                await repository.StoreDetailsAsync(reservation, payload, ct);
            }
        }
        catch (IfoodIntegrationException ex)
        {
            if (reservation.LeaseId != null) await repository.DetailsFailedAsync(reservation, ex.Code, TimeSpan.FromSeconds(30), ct);
            throw;
        }
        payload = IfoodDeliveryLocation.Preserve(payload, context.Payload);
        if (scopes != null) payload = await IfoodDeliveryLocation.TryLocateAsync(payload, context.MerchantId, context.ExternalOrderId, scopes, ct);
        var official = IfoodOrderNormalizer.Normalize(payload, context.MerchantId, context.ExternalOrderId);
        if (!options.CurrentValue.Enabled || !options.CurrentValue.ImportEnabled) throw new IfoodIntegrationException("IFOOD_DISABLED");
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Sem locks durante HTTP. Revalida revisão e autorização no commit.
        await connection.ExecuteAsync(new CommandDefinition("SELECT id FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store FOR UPDATE",
            new { Pedido = pedido, Store = store }, tx, cancellationToken: ct));
        var revision = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
SELECT s.revision FROM ifood_order_snapshots s JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
WHERE s.merchant_id=@MerchantId AND s.external_order_id=@ExternalOrderId AND s.estabelecimento_id=@Store
AND c.status='authorized' AND c.revoked_at_utc IS NULL FOR UPDATE OF s FOR SHARE OF c
""", new { context.MerchantId, context.ExternalOrderId, Store = store }, tx, cancellationToken: ct));
        if (revision != context.Revision) throw IfoodCommandStale();
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId
AND estabelecimento_id=@Store AND state IN('queued','processing','uncertain','review'))
""", new { context.MerchantId, context.ExternalOrderId, Store = store }, tx, cancellationToken: ct))) throw IfoodCommandStale();
        if (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('delivery_route_stops') IS NOT NULL", transaction: tx) &&
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE pedido_id=@Pedido AND estabelecimento_id=@Store AND stop_status IN('assigned','en_route'))",
                new { Pedido = pedido, Store = store }, tx, cancellationToken: ct)))
            throw new DeliveryDomainException(409, "IFOOD_REFRESH_ACTIVE_ROUTE", "O pedido já está em uma rota. Resolva a alteração com a operação de entrega antes de atualizar a localização.");
        if (await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_delivery_completions') IS NOT NULL", transaction: tx) &&
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM ifood_delivery_completions WHERE pedido_id=@Pedido AND estabelecimento_id=@Store AND state IN('queued','processing','uncertain','verified','review'))",
                new { Pedido = pedido, Store = store }, tx, cancellationToken: ct))) throw IfoodCommandStale();
        var recovered = JsonNode.Parse(context.Payload)?["zippyRecovery"]?.GetValue<bool>() == true;
        var root = JsonNode.Parse(official.PayloadJson)!.AsObject(); root["zippyRecovery"] = recovered;
        var validSchedule = official.ScheduledStartUtc != null && official.ScheduledEndUtc != null && official.ScheduledEndUtc >= official.ScheduledStartUtc;
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_snapshots SET payload=CAST(@Payload AS jsonb),data_is_current=TRUE,revision=revision+1,updated_at_utc=NOW(),
order_type=@OrderType,delivered_by=@DeliveredBy,currency=@Currency,order_amount=@OrderAmount,prepaid_amount=@PrepaidAmount,pending_amount=@PendingAmount,
scheduled_start_utc=@ScheduledStartUtc,scheduled_end_utc=@ScheduledEndUtc,
synchronization_state=CASE WHEN @Review OR EXISTS(SELECT 1 FROM ifood_events_inbox i WHERE i.merchant_id=@MerchantId
AND i.external_order_id=@ExternalOrderId AND i.estabelecimento_id=@Store AND i.state IN('queued','processing','review','failed')) THEN 'review'
WHEN @Recovered THEN 'pending' ELSE 'synced' END
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@Store;
UPDATE pedido SET endereco_entrega=@Address,entrega_rua=@Street,entrega_numero=@Number,entrega_cidade=@City,
entrega_bairro=@District,region=@District,entrega_estado=@State,entrega_cep=@Zip,latitude=@Latitude,longitude=@Longitude,
tipo_pagamento=@PaymentMethod,status_pagamento=@PaymentState,nome_cliente=@CustomerName,observacoes=@Observations,
value=@OrderAmount WHERE id=@Pedido AND id_estabelecimento=@Store;
INSERT INTO ifood_reconciliation_audit(merchant_id,estabelecimento_id,external_order_id,action,actor_user_id,note,previous_state)
VALUES(@MerchantId,@Store,@ExternalOrderId,'refresh_details',@User,'Dados reconsultados na API oficial; estado remoto preservado.',@PreviousState);
""", new { context.MerchantId, context.ExternalOrderId, Store = store, Pedido = pedido, User = user, Payload = root.ToJsonString(),
            official.OrderType, official.DeliveredBy, official.Currency, official.OrderAmount, official.PrepaidAmount, official.PendingAmount,
            ScheduledStartUtc = validSchedule ? official.ScheduledStartUtc : null, ScheduledEndUtc = validSchedule ? official.ScheduledEndUtc : null,
            Review = official.RequiresReview, Recovered = recovered, PreviousState = context.SynchronizationState, official.CustomerName, official.Observations,
            Address = IfoodOrderNormalizer.Text(official.Address, "formattedAddress") ?? string.Join(", ", new[] { "streetName", "streetNumber", "neighborhood", "city", "state" }.Select(key => IfoodOrderNormalizer.Text(official.Address, key)).Where(x => x != null)), Street = IfoodOrderNormalizer.Text(official.Address, "streetName"),
            Number = IfoodOrderNormalizer.Text(official.Address, "streetNumber"), City = IfoodOrderNormalizer.Text(official.Address, "city"),
            State = IfoodOrderNormalizer.Text(official.Address, "state"), Zip = IfoodOrderNormalizer.Text(official.Address, "postalCode") ?? IfoodOrderNormalizer.Text(official.Address, "zipCode"),
            District = IfoodOrderNormalizer.Text(official.Address, "neighborhood") ?? IfoodOrderNormalizer.Text(official.Address, "district"),
            official.Latitude, official.Longitude, PaymentMethod = string.Join(" / ", official.Payments.Select(p => p.Method).Distinct()), official.PaymentState }, tx, cancellationToken: ct));
        await IfoodOrderProjectionRepository.ReplaceItems(connection, tx, item, official, ct);
        await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,aggregate_version,payload,occurred_at_utc)
VALUES(@Id,@Name,@Group,@Store,@Revision,CAST(@Payload AS jsonb),NOW())
""", new { Id = Guid.NewGuid(), Name = DeliveryRealtimeEvents.DeliveryOrderUpdated, Group = DeliveryRealtimeEvents.EstablishmentGroup(store),
            Store = store, Revision = context.Revision + 1, Payload = JsonSerializer.Serialize(new { schemaVersion = 2, revision = context.Revision + 1, pedidoId = pedido, estabelecimentoId = store, action = "ifood_refreshed" }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static DeliveryDomainException IfoodCommandStale() => new(409, "IFOOD_COMMAND_STALE_REVISION", "O pedido mudou ou tem uma ação pendente. Atualize antes de tentar novamente.");
}
