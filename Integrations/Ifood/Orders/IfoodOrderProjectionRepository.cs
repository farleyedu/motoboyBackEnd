using System.Text.Json;
using APIBack.Hubs;
using APIBack.Integrations.Ifood.Events;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Orders;

public sealed class IfoodOrderProjectionRepository
{
    private readonly NpgsqlDataSource _source;
    public IfoodOrderProjectionRepository(NpgsqlDataSource source) => _source = source;

    public async Task<string> ProjectAsync(IfoodInboxReservation reservation, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            // Lê o payload autoritativo da inbox; caller não pode substituir identidade/conteúdo.
            var item = await connection.QuerySingleOrDefaultAsync<IfoodInboxReservation>(new CommandDefinition("""
SELECT id AS Id,merchant_id AS MerchantId,estabelecimento_id AS EstabelecimentoId,event_id AS EventId,
external_order_id AS ExternalOrderId,event_code AS EventCode,payload::text AS PayloadJson,lease_id AS LeaseId,attempts AS Attempts
FROM ifood_events_inbox WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp() FOR UPDATE
""", reservation, tx, cancellationToken: ct));
            if (item == null) throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
            var authorized = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
SELECT merchant_id FROM ifood_connections WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL FOR SHARE
""", item, tx, cancellationToken: ct));
            if (authorized == null) throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
            IfoodOrderProjectionEvent? ev = null; string? reason = null; string outcome = "review";
            try { ev = IfoodOrderProjectionEvent.Parse(item); }
            catch (IfoodIntegrationException ex) { reason = ex.Code; }
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))",
                new { Key = $"ifood:{item.MerchantId:D}:{item.ExternalOrderId:D}" }, tx, cancellationToken: ct));
            var snapshot = await connection.QuerySingleOrDefaultAsync<Snapshot>(new CommandDefinition("""
SELECT l.pedido_id AS PedidoId,s.revision AS Revision,s.remote_status AS RemoteStatus,s.preparation_state AS PreparationState,
s.synchronization_state AS SynchronizationState,s.payload::text AS Payload,s.order_amount AS OrderAmount,
s.prepaid_amount AS PrepaidAmount,s.pending_amount AS PendingAmount,s.currency AS Currency,
s.status_occurred_at_utc AS StatusAt,s.preparation_occurred_at_utc AS PreparationAt,s.data_occurred_at_utc AS DataAt,
s.cancellation_occurred_at_utc AS CancellationAt,s.tracking_occurred_at_utc AS TrackingAt,
s.cancellation_state AS CancellationState,s.delivery_state AS DeliveryState,s.data_is_current AS DataIsCurrent
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.merchant_id=@MerchantId AND l.external_order_id=@ExternalOrderId AND l.estabelecimento_id=@EstabelecimentoId FOR UPDATE OF s
""", item, tx, cancellationToken: ct));
            var changed = false; IfoodImportedOrder? patched = null;
            if (ev != null && Known(ev.Code))
            {
                if (snapshot == null)
                {
                    // Evento anterior ao PLC espera a importação sem gastar tentativas de falha técnica.
                    await Finish(connection, tx, item, "queued", "IFOOD_WAITING_ORDER_IMPORT", ct);
                    await tx.CommitAsync(ct); return "waiting_import";
                }
                var baseline = IfoodOrderNormalizer.Normalize(snapshot.Payload, item.MerchantId, item.ExternalOrderId!.Value).CreatedAtUtc;
                snapshot.StatusAt ??= baseline; snapshot.PreparationAt ??= baseline; snapshot.DataAt ??= baseline;
                var code = ev.Code;
                if (code is "CONFIRMED" or "DISPATCHED" or "CONCLUDED" or "CANCELLED")
                {
                    outcome = IfoodProjectionOrdering.Decide(ev.At, snapshot.StatusAt, snapshot.RemoteStatus, code, IfoodProjectionOrdering.StatusRank);
                    if (outcome == "applied") { snapshot.RemoteStatus = code; snapshot.StatusAt = ev.At; changed = true; }
                    if (code == "CONFIRMED" && outcome != "review" && snapshot.RemoteStatus is not ("CANCELLED" or "CONCLUDED"))
                    {
                        var prep = IfoodProjectionOrdering.Decide(ev.At, snapshot.PreparationAt, snapshot.PreparationState, "accepted", IfoodProjectionOrdering.PreparationRank);
                        // Confirmação atrasada não desfaz preparo já iniciado.
                        if (prep == "applied") { snapshot.PreparationState = "accepted"; snapshot.PreparationAt = ev.At; changed = true; outcome = "applied"; }
                    }
                    if (code == "CANCELLED" && outcome == "applied") { snapshot.CancellationState = "cancelled"; snapshot.CancellationAt = ev.At; }
                }
                else if (code is "PREPARATION_STARTED" or "READY_TO_PICKUP" or "SEPARATION_STARTED" or "SEPARATION_ENDED")
                {
                    // Separação GROCERY não vira preparo FOOD por analogia.
                    if (code is "SEPARATION_STARTED" or "SEPARATION_ENDED") { outcome = "review"; reason = "IFOOD_CATEGORY_NOT_SUPPORTED"; }
                    else if (snapshot.RemoteStatus is "CANCELLED" or "CONCLUDED") outcome = "ignored";
                    else
                    {
                        var next = code == "PREPARATION_STARTED" ? "preparing" : "ready";
                        outcome = IfoodProjectionOrdering.Decide(ev.At, snapshot.PreparationAt, snapshot.PreparationState, next, IfoodProjectionOrdering.PreparationRank);
                        if (outcome == "applied") { snapshot.PreparationState = next; snapshot.PreparationAt = ev.At; changed = true; }
                    }
                }
                else if (code is "CANCELLATION_REQUESTED" or "CANCELLATION_REQUEST_FAILED")
                {
                    var next = code == "CANCELLATION_REQUESTED" ? "requested" : "rejected";
                    outcome = snapshot.RemoteStatus == "CANCELLED" ? "ignored" :
                        IfoodProjectionOrdering.Decide(ev.At, snapshot.CancellationAt, snapshot.CancellationState, next);
                    if (outcome == "applied") { snapshot.CancellationState = next; snapshot.CancellationAt = ev.At; changed = true; }
                }
                else if (code == "ORDER_PATCHED" || code == "DELIVERY_ADDRESS_CHANGE_REQUESTED")
                {
                    outcome = IfoodProjectionOrdering.Decide(ev.At, snapshot.DataAt, null, "patch");
                    if (outcome == "applied")
                    {
                        try
                        {
                            if (code != "ORDER_PATCHED") throw new IfoodIntegrationException("IFOOD_ADDRESS_CHANGE_REQUIRES_RECONCILIATION");
                            var result = IfoodOrderPatch.Apply(snapshot.Payload, ev.Metadata, item.MerchantId, item.ExternalOrderId!.Value);
                            patched = result.Order; snapshot.Payload = patched.PayloadJson;
                            snapshot.DataIsCurrent = snapshot.DataIsCurrent && result.Complete;
                        }
                        catch (IfoodIntegrationException ex) { reason = ex.Code; snapshot.DataIsCurrent = false; }
                        snapshot.DataAt = ev.At; changed = true;
                        if (!snapshot.DataIsCurrent) { outcome = "review"; reason ??= "IFOOD_PATCH_REQUIRES_RECONCILIATION"; }
                        snapshot.OrderAmount = snapshot.DataIsCurrent ? patched!.OrderAmount : null;
                        snapshot.PrepaidAmount = snapshot.DataIsCurrent ? patched!.PrepaidAmount : null;
                        snapshot.PendingAmount = snapshot.DataIsCurrent ? patched!.PendingAmount : null;
                        if (patched != null) snapshot.Currency = patched.Currency;
                    }
                }
                else if (code is "HANDSHAKE_DISPUTE" or "HANDSHAKE_SETTLEMENT")
                {
                    try { outcome = await Dispute(connection, tx, item, ev, ct); changed = outcome != "ignored"; }
                    catch (IfoodIntegrationException ex) { outcome = "review"; reason = ex.Code; }
                }
                else if (Tracking(code))
                {
                    outcome = snapshot.RemoteStatus is "CANCELLED" or "CONCLUDED" ? "ignored" :
                        IfoodProjectionOrdering.Decide(ev.At, snapshot.TrackingAt, snapshot.DeliveryState, code);
                    if (outcome == "applied") { snapshot.DeliveryState = code; snapshot.TrackingAt = ev.At; changed = true; }
                }
                else outcome = "ignored"; // RPS/DGAC/DRCR são informativos; metadata permanece na inbox.
            }
            else if (ev != null) reason = "IFOOD_EVENT_UNKNOWN_CODE";
            if (outcome == "review")
            {
                reason ??= "IFOOD_EVENT_STATE_CONFLICT";
                if (snapshot != null) { snapshot.SynchronizationState = "review"; changed = true; }
                if (snapshot != null && ev?.Code is "ORDER_PATCHED" or "DELIVERY_ADDRESS_CHANGE_REQUESTED")
                {
                    snapshot.DataIsCurrent = false;
                    snapshot.OrderAmount = snapshot.PrepaidAmount = snapshot.PendingAmount = null;
                }
            }
            await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_order_event_history(inbox_id,merchant_id,estabelecimento_id,external_order_id,event_id,event_code,occurred_at_utc,outcome,reason_code)
VALUES(@Id,@MerchantId,@EstabelecimentoId,@ExternalOrderId,@EventId,@Code,@At,@Outcome,@Reason)
""", new { item.Id, item.MerchantId, item.EstabelecimentoId, item.ExternalOrderId, item.EventId,
                Code = ev?.Code ?? item.EventCode, At = ev?.At, Outcome = outcome, Reason = reason }, tx, cancellationToken: ct));
            if (ev != null)
                await Commands.IfoodCommandEventReconciliation.ApplyAsync(connection,tx,item,ev.Code,outcome,ev.At,ct);
            if (changed && snapshot != null)
            {
                await Save(connection, tx, item, snapshot, ct);
                if (patched != null) await ReplaceItems(connection, tx, item, patched, ct);
                // CAN final arquiva o pedido; CON permanece externo e não cria prova física/cobrança local.
                if (outcome == "applied" && ev?.Code == "CANCELLED")
                    await connection.ExecuteAsync(new CommandDefinition("UPDATE pedido SET status_pedido=4 WHERE id=@PedidoId AND id_estabelecimento=@Store",
                        new { snapshot.PedidoId, Store = item.EstabelecimentoId }, tx, cancellationToken: ct));
                var payload = JsonSerializer.Serialize(new { schemaVersion = 2, pedidoId = snapshot.PedidoId, estabelecimentoId = item.EstabelecimentoId,
                    action = "ifood_projected", revision = snapshot.Revision + 1, remoteStatus = snapshot.RemoteStatus, synchronizationState = snapshot.SynchronizationState });
                await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,motoboy_id,session_id,session_epoch,aggregate_version,payload,occurred_at_utc)
VALUES(@Event,@Name,@Target,@Store,NULL,NULL,NULL,@Revision,CAST(@Payload AS jsonb),NOW())
""", new { Event = Guid.NewGuid(), Name = DeliveryRealtimeEvents.DeliveryOrderUpdated, Target = DeliveryRealtimeEvents.EstablishmentGroup(item.EstabelecimentoId),
                    Store = item.EstabelecimentoId, Revision = snapshot.Revision + 1, Payload = payload }, tx, cancellationToken: ct));
            }
            await Finish(connection, tx, item, outcome == "applied" ? "processed" : outcome, reason, ct);
            await tx.CommitAsync(ct); return outcome;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { throw new IfoodIntegrationException("IFOOD_PROJECTION_DATABASE_ERROR", retryable: true); }
    }

    private static bool Known(string code) => code is "CONFIRMED" or "PREPARATION_STARTED" or "READY_TO_PICKUP" or "SEPARATION_STARTED" or "SEPARATION_ENDED"
        or "DISPATCHED" or "CONCLUDED" or "CANCELLED" or "CANCELLATION_REQUESTED" or "CANCELLATION_REQUEST_FAILED" or "ORDER_PATCHED"
        or "HANDSHAKE_DISPUTE" or "HANDSHAKE_SETTLEMENT" or "RECOMMENDED_PREPARATION_START" or "DELIVERY_GROUP_ASSOCIATED"
        or "DELIVERY_RETURN_CODE_REQUESTED" or "DELIVERY_ADDRESS_CHANGE_REQUESTED" || Tracking(code);
    private static bool Tracking(string code) => code is "ASSIGN_DRIVER" or "GOING_TO_ORIGIN" or "ARRIVED_AT_ORIGIN" or "COLLECTED"
        or "ARRIVED_AT_DESTINATION" or "DELIVERY_RETURNING_TO_ORIGIN" or "DELIVERY_RETURNED_TO_ORIGIN";
    private static async Task Save(NpgsqlConnection c, NpgsqlTransaction tx, IfoodInboxReservation item, Snapshot s, CancellationToken ct)
        => await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_snapshots SET revision=revision+1,remote_status=@RemoteStatus,preparation_state=@PreparationState,
synchronization_state=@SynchronizationState,status_occurred_at_utc=@StatusAt,preparation_occurred_at_utc=@PreparationAt,
data_occurred_at_utc=@DataAt,cancellation_occurred_at_utc=@CancellationAt,tracking_occurred_at_utc=@TrackingAt,
cancellation_state=@CancellationState,delivery_state=@DeliveryState,data_is_current=@DataIsCurrent,payload=CAST(@Payload AS jsonb),
order_amount=@OrderAmount,prepaid_amount=@PrepaidAmount,pending_amount=@PendingAmount,currency=@Currency,updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, s.RemoteStatus, s.PreparationState, s.SynchronizationState,
            s.StatusAt, s.PreparationAt, s.DataAt, s.CancellationAt, s.TrackingAt, s.CancellationState, s.DeliveryState, s.DataIsCurrent,
            s.Payload, s.OrderAmount, s.PrepaidAmount, s.PendingAmount, s.Currency }, tx, cancellationToken: ct));
    private static async Task ReplaceItems(NpgsqlConnection c, NpgsqlTransaction tx, IfoodInboxReservation item, IfoodImportedOrder order, CancellationToken ct)
    {
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM ifood_order_items WHERE merchant_id=@MerchantId AND external_order_id=@ExternalOrderId AND estabelecimento_id=@EstabelecimentoId", item, tx, cancellationToken: ct));
        var lines = order.Items.Select((line, index) => new { ordinal=index,external_item_id=line.ExternalItemId,name=line.Name,quantity=line.Quantity,unit=line.Unit,
            unit_price=line.UnitPrice,price=line.Price,options_price=line.OptionsPrice,total_price=line.TotalPrice,observations=line.Observations,options=line.Options });
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_order_items(merchant_id,external_order_id,estabelecimento_id,ordinal,external_item_id,name,quantity,unit,unit_price,price,options_price,total_price,observations,options)
SELECT @MerchantId,@ExternalOrderId,@EstabelecimentoId,i.* FROM jsonb_to_recordset(CAST(@Lines AS jsonb)) AS i(ordinal int,external_item_id text,
name text,quantity numeric,unit text,unit_price numeric,price numeric,options_price numeric,total_price numeric,observations text,options jsonb)
""", new { item.MerchantId, item.ExternalOrderId, item.EstabelecimentoId, Lines=JsonSerializer.Serialize(lines) }, tx, cancellationToken: ct));
    }
    private static async Task Finish(NpgsqlConnection c, NpgsqlTransaction tx, IfoodInboxReservation item, string state, string? reason, CancellationToken ct)
    {
        var count = await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET state=@State,processed_at_utc=CASE WHEN @State IN('processed','ignored') THEN NOW() ELSE NULL END,
lease_id=NULL,lease_until_utc=NULL,last_error_code=@Reason,
attempts=CASE WHEN @State='queued' THEN GREATEST(0,attempts-1) ELSE attempts END,
next_attempt_at_utc=CASE WHEN @State='queued' THEN NOW()+INTERVAL '30 seconds' ELSE next_attempt_at_utc END
WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId AND state='processing' AND lease_id=@LeaseId AND lease_until_utc>clock_timestamp()
""", new { item.Id, item.MerchantId, item.EstabelecimentoId, item.LeaseId, State=state, Reason=reason }, tx, cancellationToken: ct));
        if (count != 1) throw new IfoodIntegrationException("IFOOD_EVENT_LEASE_LOST", retryable: true);
    }
    private sealed class Snapshot
    {
        public int PedidoId { get; set; } public long Revision { get; set; } public string? RemoteStatus { get; set; }
        public string PreparationState { get; set; } = ""; public string SynchronizationState { get; set; } = ""; public string Payload { get; set; } = "{}";
        public decimal? OrderAmount { get; set; } public decimal? PrepaidAmount { get; set; } public decimal? PendingAmount { get; set; } public string? Currency { get; set; }
        public DateTimeOffset? StatusAt { get; set; } public DateTimeOffset? PreparationAt { get; set; } public DateTimeOffset? DataAt { get; set; }
        public DateTimeOffset? CancellationAt { get; set; } public DateTimeOffset? TrackingAt { get; set; }
        public string CancellationState { get; set; } = "none"; public string? DeliveryState { get; set; } public bool DataIsCurrent { get; set; }
    }

    private static async Task<string> Dispute(NpgsqlConnection c, NpgsqlTransaction tx, IfoodInboxReservation item, IfoodOrderProjectionEvent ev, CancellationToken ct)
    {
        var meta = ev.Metadata;
        var text = IfoodOrderNormalizer.Text(meta, "disputeId") ?? (ev.Code == "HANDSHAKE_DISPUTE" ? IfoodOrderNormalizer.Text(meta, "id") : null);
        if (!Guid.TryParse(text, out var dispute) || dispute == Guid.Empty) throw new IfoodIntegrationException("IFOOD_DISPUTE_INVALID_ID");
        await c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0))",
            new { Key=$"ifood-dispute:{item.MerchantId:D}:{dispute:D}" },tx,cancellationToken:ct));
        // Uma disputa não pode mudar de pedido/loja em evento posterior.
        var mismatch = await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_disputes WHERE merchant_id=@MerchantId AND dispute_id=@Dispute AND (external_order_id<>@ExternalOrderId OR estabelecimento_id<>@EstabelecimentoId))
OR EXISTS(SELECT 1 FROM ifood_dispute_settlements WHERE merchant_id=@MerchantId AND dispute_id=@Dispute AND (external_order_id<>@ExternalOrderId OR estabelecimento_id<>@EstabelecimentoId))
""", new { item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,Dispute=dispute }, tx, cancellationToken: ct));
        if (mismatch) throw new IfoodIntegrationException("IFOOD_DISPUTE_IDENTITY_CONFLICT");
        var raw = SanitizeMetadata(meta, item.MerchantId, item.ExternalOrderId!.Value);
        if (ev.Code == "HANDSHAKE_DISPUTE")
        {
            var versioned=await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('ifood_cancellation_reason_snapshots') IS NOT NULL",transaction:tx,cancellationToken:ct));
            var settledAt = await c.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
                "SELECT settled_at_utc FROM ifood_disputes WHERE merchant_id=@MerchantId AND dispute_id=@Dispute FOR UPDATE",
                new { item.MerchantId, Dispute=dispute },tx,cancellationToken:ct));
            if (settledAt.HasValue && ev.At>settledAt) return "review";
            var deadline = IfoodOrderNormalizer.Text(meta, "expiresAt");
            var timeout = IfoodOrderNormalizer.Text(meta, "timeoutAction");
            if (deadline == null || !System.Text.RegularExpressions.Regex.IsMatch(deadline, @"(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(deadline, out var expires) || expires.ToUniversalTime()<ev.At || timeout == null)
                throw new IfoodIntegrationException("IFOOD_DISPUTE_INVALID_DEADLINE");
            var action = IfoodOrderNormalizer.Text(meta, "action");
            var review = timeout is not ("ACCEPT_CANCELLATION" or "REJECT_CANCELLATION" or "VOID") ||
                action is not ("CANCELLATION" or "PARTIAL_CANCELLATION" or "PROPOSED_AMOUNT_REFUND");
            var count = await c.ExecuteAsync(new CommandDefinition($"""
INSERT INTO ifood_disputes(merchant_id,dispute_id,external_order_id,estabelecimento_id,expires_at_utc,timeout_action,state,payload,opened_at_utc)
VALUES(@MerchantId,@Dispute,@ExternalOrderId,@EstabelecimentoId,@Expires,@Timeout,@State,CAST(@Payload AS jsonb),@At)
ON CONFLICT(merchant_id,dispute_id) DO UPDATE SET expires_at_utc=EXCLUDED.expires_at_utc,timeout_action=EXCLUDED.timeout_action,
payload=EXCLUDED.payload,opened_at_utc=EXCLUDED.opened_at_utc,updated_at_utc=NOW(),
{(versioned?"revision=ifood_disputes.revision+1,":"")}
state=CASE WHEN ifood_disputes.settled_at_utc IS NULL THEN EXCLUDED.state ELSE ifood_disputes.state END
WHERE ifood_disputes.opened_at_utc IS NULL OR ifood_disputes.opened_at_utc<EXCLUDED.opened_at_utc
""", new { item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,Dispute=dispute,Expires=expires.ToUniversalTime(),Timeout=timeout,
                State=review?"review":"open",Payload=raw,ev.At }, tx, cancellationToken: ct));
            if (count == 0)
            {
                var same = await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT opened_at_utc<>@At OR payload=CAST(@Payload AS jsonb) FROM ifood_disputes WHERE merchant_id=@MerchantId AND dispute_id=@Dispute",
                    new { item.MerchantId,Dispute=dispute,ev.At,Payload=raw }, tx, cancellationToken: ct));
                return same ? "ignored" : "review";
            }
            await ApplySettlement(c, tx, item, dispute, ct);
            var premature = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM ifood_dispute_settlements WHERE merchant_id=@MerchantId AND dispute_id=@Dispute AND occurred_at_utc<@At)",
                new { item.MerchantId,Dispute=dispute,ev.At },tx,cancellationToken:ct));
            if (premature)
            {
                await c.ExecuteAsync(new CommandDefinition("UPDATE ifood_disputes SET state='review' WHERE merchant_id=@MerchantId AND dispute_id=@Dispute",
                    new { item.MerchantId,Dispute=dispute },tx,cancellationToken:ct));
                return "review";
            }
            return review ? "review" : "applied";
        }
        var status = IfoodOrderNormalizer.Text(meta, "status") ?? throw new IfoodIntegrationException("IFOOD_SETTLEMENT_INVALID_STATUS");
        var existing = await c.QuerySingleOrDefaultAsync<Settlement>(new CommandDefinition("SELECT occurred_at_utc AS At,status AS Status,payload::text AS Payload FROM ifood_dispute_settlements WHERE merchant_id=@MerchantId AND dispute_id=@Dispute FOR UPDATE",
            new { item.MerchantId,Dispute=dispute }, tx, cancellationToken: ct));
        if (existing != null && ev.At <= existing.At)
        {
            if (ev.At < existing.At) return "ignored";
            var same = await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT payload=CAST(@Payload AS jsonb) FROM ifood_dispute_settlements WHERE merchant_id=@MerchantId AND dispute_id=@Dispute",
                new { item.MerchantId,Dispute=dispute,Payload=raw }, tx, cancellationToken: ct));
            return same ? "ignored" : "review";
        }
        if (existing?.Status is "ACCEPTED" or "REJECTED" or "EXPIRED" && existing.Status != status) return "review";
        var openedAt = await c.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT opened_at_utc FROM ifood_disputes WHERE merchant_id=@MerchantId AND dispute_id=@Dispute FOR UPDATE",
            new { item.MerchantId,Dispute=dispute },tx,cancellationToken:ct));
        if (openedAt.HasValue && ev.At<openedAt) return "review";
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_dispute_settlements(merchant_id,dispute_id,external_order_id,estabelecimento_id,occurred_at_utc,status,payload)
VALUES(@MerchantId,@Dispute,@ExternalOrderId,@EstabelecimentoId,@At,@Status,CAST(@Payload AS jsonb))
ON CONFLICT(merchant_id,dispute_id) DO UPDATE SET occurred_at_utc=EXCLUDED.occurred_at_utc,status=EXCLUDED.status,payload=EXCLUDED.payload
""", new { item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,Dispute=dispute,ev.At,Status=status,Payload=raw }, tx, cancellationToken: ct));
        await ApplySettlement(c, tx, item, dispute, ct);
        return status is "ACCEPTED" or "REJECTED" or "EXPIRED" or "ALTERNATIVE_REPLIED" ? "applied" : "review";
    }
    private static async Task<int> ApplySettlement(NpgsqlConnection c, NpgsqlTransaction tx, IfoodInboxReservation item, Guid dispute, CancellationToken ct)
    {
        var versioned=await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass('ifood_cancellation_reason_snapshots') IS NOT NULL",transaction:tx,cancellationToken:ct));
        return await c.ExecuteAsync(new CommandDefinition($"""
UPDATE ifood_disputes d SET settlement=s.payload,settled_at_utc=s.occurred_at_utc,updated_at_utc=NOW(),
{(versioned?"revision=d.revision+1,":"")}
state=CASE s.status WHEN 'EXPIRED' THEN 'expired' WHEN 'ACCEPTED' THEN 'resolved' WHEN 'REJECTED' THEN 'resolved'
WHEN 'ALTERNATIVE_REPLIED' THEN 'responded' ELSE 'review' END
FROM ifood_dispute_settlements s WHERE d.merchant_id=s.merchant_id AND d.dispute_id=s.dispute_id
AND d.external_order_id=s.external_order_id AND d.estabelecimento_id=s.estabelecimento_id
AND d.merchant_id=@MerchantId AND d.external_order_id=@ExternalOrderId AND d.estabelecimento_id=@EstabelecimentoId AND d.dispute_id=@Dispute
AND (d.opened_at_utc IS NULL OR d.opened_at_utc<=s.occurred_at_utc)
AND (d.settled_at_utc IS NULL OR d.settled_at_utc<=s.occurred_at_utc)
""", new { item.MerchantId,item.ExternalOrderId,item.EstabelecimentoId,Dispute=dispute }, tx, cancellationToken: ct));
    }
    private static string SanitizeMetadata(JsonElement meta, Guid merchant, Guid order)
    {
        var wrapper = JsonSerializer.Serialize(new { id=order,merchant=new { id=merchant },metadata=meta });
        using var sanitized = JsonDocument.Parse(IfoodOrderNormalizer.Sanitize(wrapper, merchant, order));
        return sanitized.RootElement.GetProperty("metadata").GetRawText();
    }
    private sealed class Settlement { public DateTimeOffset At { get; set; } public string Status { get; set; } = ""; public string Payload { get; set; } = "{}"; }
}
