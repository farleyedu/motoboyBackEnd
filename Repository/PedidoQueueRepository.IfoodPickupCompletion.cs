using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private static async Task<OfficialCompletionOrder> RequireOfficialPickupOrder(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, IfoodOptions options)
    {
        var row = await c.QuerySingleOrDefaultAsync<OfficialCompletionOrder>("""
SELECT l.merchant_id AS Merchant,l.external_order_id AS "Order",l.display_number AS DisplayNumber,s.revision AS Revision,s.payload::text AS Payload
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
JOIN ifood_connections a USING(merchant_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido AND s.order_type='TAKEOUT'
AND a.status='authorized' AND a.authorized_at_utc IS NOT NULL AND a.revoked_at_utc IS NULL
AND s.data_is_current AND s.synchronization_state='synced' AND s.remote_status='CONFIRMED' AND s.preparation_state='ready'
AND s.cancellation_state IN('none','rejected','')
AND NOT EXISTS(SELECT 1 FROM ifood_outbound_commands o WHERE o.estabelecimento_id=l.estabelecimento_id AND o.merchant_id=l.merchant_id AND o.external_order_id=l.external_order_id AND o.state IN('queued','processing','uncertain','review'))
FOR SHARE OF l,s,a
""", new { Store = store, Pedido = pedido }, tx);
        if (row == null) throw new DeliveryDomainException(409, "IFOOD_PICKUP_COMPLETION_NOT_ELIGIBLE", "A retirada oficial não está pronta ou exige revisão.");
        var blocked = IfoodCompletionCapability(options, store, row.Merchant);
        if (blocked != null) throw new DeliveryDomainException(409, blocked, "Confirmação oficial de retirada indisponível.");
        return row;
    }

    internal async Task<IfoodCompletionOperationDto> EnqueueIfoodPickupAsync(Guid store, int user, int pedido,
        IfoodPickupCompletionRequest request, IfoodOptions options, IfoodSecretProtector secrets, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || pedido <= 0 || user <= 0 || request.ExpectedRevision <= 0 ||
            string.IsNullOrWhiteSpace(request.Codigo) || request.Codigo.Length > 32 || request.Codigo.Any(char.IsControl))
            throw new DeliveryDomainException(422, "IFOOD_PICKUP_COMPLETION_INVALID", "Confira o pedido e o código oficial da retirada.");
        var opened = await OpenCompletionTransaction(ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        if (!await HasIfoodCompletionAsync(c, tx)) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_DISABLED", "Confirmação oficial indisponível.");
        var found = await c.ExecuteScalarAsync<int?>("SELECT id FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store FOR UPDATE", new { Pedido = pedido, Store = store }, tx);
        if (found == null) throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido não encontrado nesta loja.");
        var purpose = "completion_" + request.OperationId.ToString("N"); var serialized = JsonSerializer.Serialize(request);
        var existing = await c.QuerySingleOrDefaultAsync<OfficialCompletionOperation>("SELECT " + OfficialOperationColumns + " FROM ifood_delivery_completions WHERE operation_id=@Operation FOR UPDATE", new { Operation = request.OperationId }, tx);
        if (existing != null)
        {
            if (existing.Kind != "takeout" || existing.Store != store || existing.Pedido != pedido ||
                existing.Hash != secrets.Fingerprint(serialized, purpose, existing.Hash.Split('.')[0]))
                throw new DeliveryDomainException(409, "IDEMPOTENCY_CONFLICT", "Esta identificação já foi usada com outra confirmação.");
            var previous = await ReadOfficialOperation(c, tx, existing.User, existing.Operation, ct);
            await tx.CommitAsync(ct); return previous!;
        }
        var official = await RequireOfficialPickupOrder(c, tx, store, pedido, options);
        if (official.Revision != request.ExpectedRevision) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_STALE_REVISION", "O pedido oficial mudou. Confira novamente.");
        var order = IfoodOrderNormalizer.Normalize(official.Payload, official.Merchant, official.Order);
        var collect = order.AmountToCollect ?? throw new DeliveryDomainException(409, "IFOOD_PAYMENT_REVIEW_REQUIRED", "Confira os valores oficiais com a loja.");
        var payment = new DeliveryCompletionRequest { OperationId = request.OperationId, ExpectedPedidoId = pedido, ExpectedVersion = request.ExpectedRevision, Payments = request.Payments };
        DeliveryCompletionRules.Validate(payment, collect, collect > 0); ValidateOfficialPayments(payment, order);
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND pedido_id=@Pedido AND state<>'rejected')", new { Store = store, Pedido = pedido }, tx))
            throw new DeliveryDomainException(409, "IFOOD_COMPLETION_ACTIVE_OPERATION", "Consulte a confirmação existente antes de reenviar.");
        var safe = JsonSerializer.Deserialize<IfoodPickupCompletionRequest>(serialized)!; safe.Codigo = "";
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_delivery_completions(operation_id,kind,estabelecimento_id,merchant_id,external_order_id,pedido_id,stop_id,
motoboy_id,user_id,session_id,session_epoch,expected_revision,payload_hash,request,manifest,code_envelope,display_number,order_total,amount_to_collect,paid_before)
VALUES(@Operation,'takeout',@Store,@Merchant,@Order,@Pedido,NULL,0,@User,'00000000-0000-0000-0000-000000000000',0,@Revision,@Hash,CAST(@Request AS jsonb),'{}',@Envelope,@Display,@Total,@Collect,@Paid)
""", new { Operation = request.OperationId, Store = store, official.Merchant, official.Order, Pedido = pedido, User = user, official.Revision,
            Hash = secrets.Fingerprint(serialized, purpose), Request = JsonSerializer.Serialize(safe), Envelope = secrets.Protect(request.Codigo, purpose),
            Display = official.DisplayNumber, Total = order.OrderAmount, Collect = collect, Paid = collect == 0 }, tx, cancellationToken: ct));
        var result = await ReadOfficialOperation(c, tx, user, request.OperationId, ct); await tx.CommitAsync(ct); return result!;
    }

    internal async Task<IfoodPickupCompletionContext> GetIfoodPickupContextAsync(Guid store, int pedido, IfoodOptions options, CancellationToken ct)
    {
        var opened = await OpenCompletionTransaction(ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        var identity = (await IfoodOrderReadModel.IdentitiesAsync(c, store, new[] { pedido }, tx)).GetValueOrDefault(pedido);
        if (identity == null) throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido iFood não encontrado nesta loja.");
        var result = new IfoodPickupCompletionContext { IdentidadeExterna = identity };
        if (!await HasIfoodCompletionAsync(c, tx)) result.BlockedCode = "IFOOD_COMPLETION_DISABLED";
        else
        {
            try { await RequireOfficialPickupOrder(c, tx, store, pedido, options); }
            catch (DeliveryDomainException ex) { result.BlockedCode = ex.Code; }
            var active = await c.QuerySingleOrDefaultAsync<(Guid Operation, int User)>("SELECT operation_id AS Operation,user_id AS User FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND pedido_id=@Pedido AND state<>'rejected'", new { Store = store, Pedido = pedido }, tx);
            if (active.Operation != Guid.Empty)
            {
                result.Operation = await ReadOfficialOperation(c, tx, active.User, active.Operation, ct);
                result.BlockedCode = "IFOOD_COMPLETION_ACTIVE_OPERATION";
            }
        }
        await tx.CommitAsync(ct); return result;
    }

    internal async Task<IfoodCompletionOperationDto?> GetIfoodCompletionForStoreAsync(Guid store, Guid operation, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        if (!await HasIfoodCompletionAsync(c)) return null;
        var owner = await c.ExecuteScalarAsync<int?>("SELECT user_id FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND operation_id=@Operation", new { Store = store, Operation = operation });
        return owner.HasValue ? await ReadOfficialOperation(c, null, owner.Value, operation, ct) : null;
    }

    private static async Task FinalizeOfficialPickup(NpgsqlConnection c, NpgsqlTransaction tx, OfficialCompletionOperation op, CancellationToken ct)
    {
        var valid = await c.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM ifood_order_snapshots s JOIN ifood_connections a USING(merchant_id,estabelecimento_id)
WHERE s.estabelecimento_id=@Store AND s.merchant_id=@Merchant AND s.external_order_id=@Order AND s.order_type='TAKEOUT'
AND s.remote_status<>'CANCELLED' AND s.data_is_current AND s.synchronization_state='synced'
AND s.order_amount=@OrderTotal AND s.pending_amount=@Collect AND (s.revision=@Revision OR s.remote_status='CONCLUDED')
AND a.status='authorized' AND a.authorized_at_utc IS NOT NULL AND a.revoked_at_utc IS NULL FOR SHARE OF s,a)
""", new { op.Store, op.Merchant, op.Order, op.OrderTotal, op.Collect, op.Revision }, tx);
        if (!valid)
        {
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='review',last_error_code='IFOOD_COMPLETION_CONTEXT_CHANGED' WHERE operation_id=@Operation", new { op.Operation }, tx);
            return;
        }
        var request = JsonSerializer.Deserialize<IfoodPickupCompletionRequest>(op.Request)!;
        var receipt = new IfoodPickupReceiptDto { OperationId = op.Operation, PedidoId = op.Pedido, ExternalDisplayNumber = op.DisplayNumber,
            ConfirmedAtUtc = DateTimeOffset.UtcNow, CodeChecked = op.CodeChecked, OfficialConfirmationEvidence = op.Evidence,
            OfficialOrderTotal = op.OrderTotal, AmountCollected = op.Collect, Payments = request.Payments };
        // Retirada não registra parada, entrega física, horário de entrega, sessão ou remuneração de motoboy.
        await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_delivery_completions SET finalization_transaction_id=pg_current_xact_id() WHERE operation_id=@Operation;
UPDATE pedido SET status_pedido=3,status_pagamento=CASE WHEN @Collect>0 THEN 'pago' ELSE status_pagamento END WHERE id=@Pedido AND id_estabelecimento=@Store;
UPDATE ifood_delivery_completions SET state='succeeded',pickup_receipt=CAST(@Receipt AS jsonb),finished_at_utc=NOW(),last_error_code=NULL,code_envelope=NULL WHERE operation_id=@Operation;
INSERT INTO delivery_realtime_outbox(event_id,event_name,target_group,estabelecimento_id,motoboy_id,session_id,session_epoch,aggregate_version,payload,occurred_at_utc)
VALUES(@Event,@Name,@Group,@Store,NULL,NULL,NULL,@Revision,CAST(@Payload AS jsonb),NOW())
""", new { op.Operation, op.Pedido, op.Store, op.Collect, op.Revision, Receipt = JsonSerializer.Serialize(receipt), Event = Guid.NewGuid(),
            Name = DeliveryRealtimeEvents.DeliveryOrderUpdated, Group = DeliveryRealtimeEvents.EstablishmentGroup(op.Store), Payload = JsonSerializer.Serialize(new { schemaVersion = 2, pedidoId = op.Pedido, action = "ifood_pickup_confirmed", operationId = op.Operation }) }, tx, cancellationToken: ct));
    }
}
