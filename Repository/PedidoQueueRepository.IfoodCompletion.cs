using System.Text.Json;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Completion;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed partial class PedidoQueueRepository
{
    private sealed class OfficialCompletionOrder
    {
        public Guid Merchant { get; set; }
        public Guid Order { get; set; }
        public long Revision { get; set; }
        public string Payload { get; set; } = "";
        public string? DisplayNumber { get; set; }
    }
    private sealed class OfficialCompletionOperation
    {
        public Guid Operation { get; set; }
        public Guid Store { get; set; }
        public Guid Merchant { get; set; }
        public Guid Order { get; set; }
        public int Rider { get; set; }
        public int User { get; set; }
        public Guid Session { get; set; }
        public long Epoch { get; set; }
        public int Pedido { get; set; }
        public long Stop { get; set; }
        public long Revision { get; set; }
        public string State { get; set; } = "";
        public string Kind { get; set; } = "delivery";
        public string Hash { get; set; } = "";
        public string Request { get; set; } = "";
        public string Manifest { get; set; } = "";
        public string? CodeEnvelope { get; set; }
        public Guid? Lease { get; set; }
        public bool CodeChecked { get; set; }
        public string? Evidence { get; set; }
        public string? DisplayNumber { get; set; }
        public decimal? OrderTotal { get; set; }
        public decimal Collect { get; set; }
        public bool PaidBefore { get; set; }
    }
    private const string OfficialOperationColumns = """
operation_id AS Operation,estabelecimento_id AS Store,merchant_id AS Merchant,external_order_id AS "Order",
motoboy_id AS Rider,user_id AS "User",session_id AS Session,session_epoch AS Epoch,pedido_id AS Pedido,stop_id AS Stop,
expected_revision AS Revision,state AS State,kind AS Kind,payload_hash AS Hash,request::text AS Request,manifest::text AS Manifest,code_envelope AS CodeEnvelope,
lease_id AS Lease,code_checked AS CodeChecked,evidence AS Evidence,display_number AS DisplayNumber,
order_total AS OrderTotal,amount_to_collect AS Collect,paid_before AS PaidBefore
""";

    internal static Task<bool> HasIfoodCompletionAsync(NpgsqlConnection c, NpgsqlTransaction? tx = null) =>
        c.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_delivery_completions') IS NOT NULL", transaction: tx);

    private static async Task<bool> IsOfficialOrderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido)
    {
        if (!await c.ExecuteScalarAsync<bool>("SELECT to_regclass('ifood_order_links') IS NOT NULL", transaction: tx)) return false;
        return await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ifood_order_links WHERE estabelecimento_id=@Store AND pedido_id=@Pedido)", new { Store = store, Pedido = pedido }, tx);
    }

    internal static string? IfoodCompletionCapability(IfoodOptions? options, Guid store, Guid merchant) =>
        options is not { Enabled: true, CommandsEnabled: true, CompletionEnabled: true } ||
        !options.CommandMerchants.Any(m => m.MerchantId == merchant && m.EstabelecimentoId == store)
            ? "IFOOD_COMPLETION_DISABLED" : null;

    private static async Task RequireCompletionSession(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int rider, int user, Guid session, long epoch)
    {
        var valid = await c.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM motoboy_active_sessions WHERE session_id=@Session AND session_epoch=@Epoch
 AND motoboy_id=@Rider AND id_estabelecimento=@Store AND id_usuario=@User AND ended_at_utc IS NULL AND revoked_at IS NULL AND expires_at_utc>clock_timestamp())
""", new { Session = session, Epoch = epoch, Rider = rider, Store = store, User = user }, tx);
        if (!valid) throw new DeliveryDomainException(409, "SESSION_CHANGED", "Recupere seu turno antes de concluir.");
    }

    private static async Task<OfficialCompletionOrder> RequireOfficialCompletionOrder(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, IfoodOptions options)
    {
        var row = await c.QuerySingleOrDefaultAsync<OfficialCompletionOrder>("""
SELECT l.merchant_id AS Merchant,l.external_order_id AS "Order",l.display_number AS DisplayNumber,
s.revision AS Revision,s.payload::text AS Payload
FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
WHERE l.estabelecimento_id=@Store AND l.pedido_id=@Pedido FOR SHARE OF l,s
""", new { Store = store, Pedido = pedido }, tx);
        if (row == null) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_NOT_APPLICABLE", "A confirmação oficial não se aplica a este pedido.");
        var blocked = IfoodCompletionCapability(options, store, row.Merchant) ?? await IfoodRoutingBlockAsync(c, tx, store, pedido, true);
        if (blocked != null) throw new DeliveryDomainException(409, blocked, "A confirmação iFood está bloqueada. Atualize a entrega.");
        return row;
    }

    internal async Task<IfoodCompletionOperationDto> EnqueueIfoodCompletionAsync(Guid store, int rider, int user, Guid session, long epoch,
        DeliveryCompletionRequest request, IfoodOptions options, IfoodSecretProtector secrets, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedPedidoId <= 0 || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.Codigo) || request.Codigo.Length > 32 || request.Codigo.Any(char.IsControl))
            throw new DeliveryDomainException(422, "IFOOD_DELIVERY_CODE_REQUIRED", "Informe o código do cliente para concluir oficialmente.");
        var opened = await OpenCompletionTransaction(ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        if (!await HasIfoodCompletionAsync(c, tx)) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_DISABLED", "Confirmação oficial indisponível.");
        var version = await LockQueuesAsync(c, tx, store, rider);
        var purpose = "completion_" + request.OperationId.ToString("N");
        var serialized = JsonSerializer.Serialize(request);
        var existing = await c.QuerySingleOrDefaultAsync<OfficialCompletionOperation>("SELECT " + OfficialOperationColumns + " FROM ifood_delivery_completions WHERE operation_id=@Operation FOR UPDATE", new { Operation = request.OperationId }, tx);
        if (existing != null)
        {
            if (existing.Store != store || existing.Rider != rider || existing.User != user ||
                existing.Hash != secrets.Fingerprint(serialized, purpose, existing.Hash.Split('.')[0]))
                throw new DeliveryDomainException(409, "IDEMPOTENCY_CONFLICT", "Esta identificação já foi usada com outra conclusão.");
            var previous = await ReadOfficialOperation(c, tx, user, request.OperationId, ct);
            await tx.CommitAsync(ct); return previous!;
        }
        if (version != request.ExpectedVersion) throw new DeliveryDomainException(409, "QUEUE_VERSION_CONFLICT", "A rota mudou. Confira a entrega.");
        await RequireCompletionSession(c, tx, store, rider, user, session, epoch);
        var current = await GetCurrentStopAsync(c, tx, store, rider, true);
        if (current?.PedidoId != request.ExpectedPedidoId) throw new DeliveryDomainException(409, "CURRENT_DELIVERY_CHANGED", "Esta não é sua entrega atual.");
        await EnsureCompletionPredatesBlockAsync(c, tx, store, current);
        var official = await RequireOfficialCompletionOrder(c, tx, store, request.ExpectedPedidoId, options);
        var row = RequireCompletionOrder(await ReadCompletionOrder(c, tx, store, rider, request.ExpectedPedidoId, ct, true));
        var manifest = await ReadChecklistAsync(c, tx, store, row.PedidoId, ct);
        DeliveryChecklistRules.Validate(row.PedidoId, manifest, request.Checklist);
        var normalized = IfoodOrderNormalizer.Normalize(official.Payload, official.Merchant, official.Order);
        var collect = normalized.AmountToCollect ?? throw new DeliveryDomainException(409, "IFOOD_PAYMENT_REVIEW_REQUIRED", "Confira os valores oficiais com a loja.");
        DeliveryCompletionRules.Validate(request, collect, collect > 0);
        ValidateOfficialPayments(request, normalized);
        if (row.RequiresProof && request.ProofId == null) throw new DeliveryDomainException(422, "PROOF_REQUIRED", "Anexe o comprovante solicitado.");
        if (request.ProofId.HasValue && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM delivery_completion_proofs WHERE id=@Id AND estabelecimento_id=@Store AND motoboy_id=@Rider AND stop_id=@Stop)", new { Id = request.ProofId, Store = store, Rider = rider, Stop = row.StopId }, tx))
            throw new DeliveryDomainException(422, "PROOF_INVALID", "Comprovante de outra entrega.");
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND pedido_id=@Pedido AND state<>'rejected')", new { Store = store, Pedido = row.PedidoId }, tx))
            throw new DeliveryDomainException(409, "IFOOD_COMPLETION_ACTIVE_OPERATION", "Consulte a confirmação existente antes de reenviar.");
        var hash = secrets.Fingerprint(serialized, purpose);
        var envelope = secrets.Protect(request.Codigo!, purpose);
        // Nunca persistir código em request, recibo ou diagnóstico.
        var safe = JsonSerializer.Deserialize<DeliveryCompletionRequest>(serialized)!; safe.Codigo = null;
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_delivery_completions(operation_id,estabelecimento_id,merchant_id,external_order_id,pedido_id,stop_id,
motoboy_id,user_id,session_id,session_epoch,expected_revision,payload_hash,request,manifest,code_envelope,display_number,order_total,amount_to_collect,paid_before)
VALUES(@Operation,@Store,@Merchant,@Order,@Pedido,@Stop,@Rider,@User,@Session,@Epoch,@Revision,@Hash,CAST(@Request AS jsonb),CAST(@Manifest AS jsonb),@Envelope,@Display,@Total,@Collect,@Paid)
""", new { Operation = request.OperationId, Store = store, official.Merchant, official.Order, Pedido = row.PedidoId, Stop = row.StopId,
            Rider = rider, User = user, Session = session, Epoch = epoch, official.Revision, Hash = hash,
            Request = JsonSerializer.Serialize(safe), Manifest = JsonSerializer.Serialize(manifest), Envelope = envelope, Display = official.DisplayNumber, Total = normalized.OrderAmount, Collect = collect, Paid = collect == 0 }, tx, cancellationToken: ct));
        var result = await ReadOfficialOperation(c, tx, user, request.OperationId, ct);
        await tx.CommitAsync(ct); return result!;
    }

    internal static void ValidateOfficialPayments(DeliveryCompletionRequest request, IfoodImportedOrder order)
    {
        var offline = order.Payments.Where(p => p.Type == "OFFLINE").ToList();
        string? Method(string? method) => method switch { "CASH" => "dinheiro", "PIX" => "pix", "DEBIT" => "debito", "CREDIT" => "credito", _ => null };
        if (offline.Any(p => Method(p.Method) == null || p.Currency != "BRL" || !p.Value.HasValue))
            throw new DeliveryDomainException(409, "IFOOD_PAYMENT_METHOD_REVIEW_REQUIRED", "O meio de pagamento oficial precisa ser conferido pela loja.");
        var expected = offline.GroupBy(p => Method(p.Method)!).ToDictionary(g => g.Key, g => g.Sum(p => p.Value!.Value));
        var actual = request.Payments.GroupBy(p => p.Method).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        if (expected.Count != actual.Count || expected.Any(p => !actual.TryGetValue(p.Key, out var value) || value != p.Value))
            throw new DeliveryDomainException(409, "IFOOD_PAYMENT_METHOD_MISMATCH", "Confira o recebimento conforme os meios e valores oficiais.");
    }

    private static async Task<IfoodCompletionOperationDto?> ReadOfficialOperation(NpgsqlConnection c, NpgsqlTransaction? tx, int user, Guid operation, CancellationToken ct)
    {
        var row = await c.QuerySingleOrDefaultAsync<IfoodCompletionOperationDto>(new CommandDefinition("""
SELECT operation_id AS OperationId,pedido_id AS PedidoId,state AS State,kind AS Kind,last_error_code AS LastErrorCode,
created_at_utc AS CreatedAtUtc,send_started_at_utc AS SendStartedAtUtc,finished_at_utc AS FinishedAtUtc,code_checked AS CodeChecked
FROM ifood_delivery_completions WHERE operation_id=@Operation AND user_id=@User
""", new { Operation = operation, User = user }, tx, cancellationToken: ct));
        if (row != null)
        {
            var receipt = await c.ExecuteScalarAsync<string?>("SELECT receipt::text FROM delivery_completions WHERE operation_id=@Operation AND user_id=@User", new { Operation = operation, User = user }, tx);
            if (receipt != null) row.Receipt = JsonSerializer.Deserialize<DeliveryReceipt>(receipt);
            if (row.Kind == "takeout")
            {
                var pickup = await c.ExecuteScalarAsync<string?>("SELECT pickup_receipt::text FROM ifood_delivery_completions WHERE operation_id=@Operation AND user_id=@User", new { Operation = operation, User = user }, tx);
                if (pickup != null) row.PickupReceipt = JsonSerializer.Deserialize<IfoodPickupReceiptDto>(pickup);
            }
        }
        return row;
    }
    internal async Task<IfoodCompletionOperationDto?> GetIfoodCompletionAsync(int user, Guid operation, CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        return await HasIfoodCompletionAsync(c) ? await ReadOfficialOperation(c, null, user, operation, ct) : null;
    }
    internal async Task<IReadOnlyList<Guid>> PendingIfoodCompletionsAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        if (!await HasIfoodCompletionAsync(c)) return Array.Empty<Guid>();
        return (await c.QueryAsync<Guid>(new CommandDefinition("SELECT operation_id FROM ifood_delivery_completions WHERE state='queued' AND next_attempt_at_utc<=NOW() ORDER BY created_at_utc LIMIT 5", cancellationToken: ct))).ToList();
    }

    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, OfficialCompletionOperation? Operation)> LockOfficialOperation(Guid operation, CancellationToken ct)
    {
        var opened = await OpenCompletionTransaction(ct); var c = opened.Connection; var tx = opened.Transaction;
        try
        {
            var preview = await c.QuerySingleOrDefaultAsync<OfficialCompletionOperation>("SELECT " + OfficialOperationColumns + " FROM ifood_delivery_completions WHERE operation_id=@Operation", new { Operation = operation }, tx);
            if (preview != null)
            {
                if (preview.Kind == "delivery") await LockQueuesAsync(c, tx, preview.Store, preview.Rider);
                await c.ExecuteScalarAsync<int>("SELECT id FROM pedido WHERE id=@Pedido AND id_estabelecimento=@Store FOR UPDATE", new { preview.Pedido, preview.Store }, tx);
                preview = await c.QuerySingleAsync<OfficialCompletionOperation>("SELECT " + OfficialOperationColumns + " FROM ifood_delivery_completions WHERE operation_id=@Operation FOR UPDATE", new { Operation = operation }, tx);
            }
            return (c, tx, preview);
        }
        catch { await tx.DisposeAsync(); await c.DisposeAsync(); throw; }
    }

    private static async Task RevalidateOfficialOperation(NpgsqlConnection c, NpgsqlTransaction tx, OfficialCompletionOperation op, IfoodOptions options)
    {
        if (op.Kind == "takeout")
        {
            await EnsureCompletionPredatesBlockAsync(c, tx, op.Store, null);
            var pickup = await RequireOfficialPickupOrder(c, tx, op.Store, op.Pedido, options);
            if (pickup.Revision != op.Revision) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_STALE_REVISION", "O pedido oficial mudou. Confira novamente.");
            return;
        }
        await RequireCompletionSession(c, tx, op.Store, op.Rider, op.User, op.Session, op.Epoch);
        var current = await GetCurrentStopAsync(c, tx, op.Store, op.Rider, true);
        if (current?.Id != op.Stop) throw new DeliveryDomainException(409, "CURRENT_DELIVERY_CHANGED", "A entrega atual mudou.");
        await EnsureCompletionPredatesBlockAsync(c, tx, op.Store, current);
        var official = await RequireOfficialCompletionOrder(c, tx, op.Store, op.Pedido, options);
        if (official.Revision != op.Revision) throw new DeliveryDomainException(409, "IFOOD_COMPLETION_STALE_REVISION", "O pedido oficial mudou. Confira novamente.");
    }

    private sealed class CompletionCommercialState
    {
        public string Situacao { get; set; } = "";
        public DateTimeOffset? BloqueadoEm { get; set; }
    }

    private static async Task EnsureCompletionPredatesBlockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, StopRow? current)
    {
        var state = await c.QuerySingleAsync<CompletionCommercialState>(
            "SELECT situacao_comercial AS Situacao, bloqueado_em AS BloqueadoEm FROM estabelecimentos WHERE id=@Store FOR SHARE",
            new { Store = store }, tx);
        if (state.Situacao == SituacoesComerciais.Implantacao ||
            (state.Situacao == SituacoesComerciais.Bloqueado &&
             (current?.StartedAtUtc == null || !state.BloqueadoEm.HasValue || current.StartedAtUtc > state.BloqueadoEm)))
            throw new DeliveryDomainException(403, SituacaoComercialRules.CodigoBloqueado,
                "Somente entregas ja em rota podem ser concluidas.");
    }

    internal async Task<IfoodCompletionReservation?> ClaimIfoodCompletionAsync(Guid operation, IfoodOptions options, CancellationToken ct)
    {
        var opened = await LockOfficialOperation(operation, ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        var op = opened.Operation;
        if (op?.State != "queued") return null;
        try { await RevalidateOfficialOperation(c, tx, op, options); }
        catch (DeliveryDomainException ex)
        {
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='rejected',code_envelope=NULL,last_error_code=@Code,finished_at_utc=NOW() WHERE operation_id=@Operation", new { op.Operation, Code = ex.Code }, tx);
            await tx.CommitAsync(ct); return null;
        }
        var lease = Guid.NewGuid();
        await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='processing',lease_id=@Lease,lease_until_utc=clock_timestamp()+INTERVAL '5 minutes' WHERE operation_id=@Operation", new { Lease = lease, op.Operation }, tx);
        await tx.CommitAsync(ct);
        return new(op.Operation, lease, op.Store, op.Merchant, op.Order, op.Rider, op.Pedido, op.CodeEnvelope!);
    }

    internal async Task MarkIfoodCompletionSendingAsync(IfoodCompletionReservation reservation, IfoodOptions options, CancellationToken ct)
    {
        var opened = await LockOfficialOperation(reservation.Operation, ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        var op = opened.Operation!;
        if (op.State != "processing" || op.Lease != reservation.Lease) throw new IfoodIntegrationException("IFOOD_COMPLETION_LEASE_LOST");
        await RevalidateOfficialOperation(c, tx, op, options);
        var changed = await c.ExecuteAsync("""
UPDATE ifood_delivery_completions SET send_started_at_utc=clock_timestamp(),code_envelope=NULL
WHERE operation_id=@Operation AND lease_id=@Lease AND lease_until_utc>clock_timestamp() AND expires_at_utc>clock_timestamp() AND send_started_at_utc IS NULL
""", new { reservation.Operation, reservation.Lease }, tx);
        if (changed != 1) throw new IfoodIntegrationException("IFOOD_COMPLETION_LEASE_LOST");
        await tx.CommitAsync(ct);
    }
    internal async Task SaveIfoodCompletionResultAsync(IfoodCompletionReservation reservation, IfoodDeliveryCodeResult result, CancellationToken ct)
    {
        var opened = await LockOfficialOperation(reservation.Operation, ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        await c.ExecuteAsync("""
UPDATE ifood_delivery_completions SET state=@State,lease_id=NULL,lease_until_utc=NULL,code_envelope=NULL,
code_checked=(@State='verified'),evidence=CASE WHEN @State='verified' THEN 'verify_delivery_code' ELSE NULL END,
http_status=@Status,last_error_code=@Code,finished_at_utc=CASE WHEN @State='rejected' THEN NOW() ELSE NULL END
WHERE operation_id=@Operation AND state='processing' AND lease_id=@Lease AND send_started_at_utc IS NOT NULL
""", new { reservation.Operation, reservation.Lease, result.State, Status = result.HttpStatus, result.Code }, tx);
        await tx.CommitAsync(ct);
    }
    internal async Task ReleaseIfoodCompletionAsync(IfoodCompletionReservation reservation, CancellationToken ct)
    {
        var opened = await LockOfficialOperation(reservation.Operation, ct); await using var c = opened.Connection; await using var tx = opened.Transaction;
        await c.ExecuteAsync("""
UPDATE ifood_delivery_completions SET state=CASE WHEN send_started_at_utc IS NULL THEN 'queued' ELSE 'uncertain' END,
lease_id=NULL,lease_until_utc=NULL,next_attempt_at_utc=NOW()+INTERVAL '30 seconds',last_error_code='IFOOD_COMPLETION_RECOVERY_REQUIRED'
WHERE operation_id=@Operation AND state='processing' AND lease_id=@Lease
""", new { reservation.Operation, reservation.Lease }, tx); await tx.CommitAsync(ct);
    }

    internal async Task RecoverIfoodCompletionsAsync(CancellationToken ct)
    {
        await using var c = await _dataSource.OpenConnectionAsync(ct);
        if (!await HasIfoodCompletionAsync(c)) return;
        // Só reservas comprovadamente anteriores ao envio podem voltar à fila.
        await c.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_delivery_completions SET state=CASE WHEN send_started_at_utc IS NOT NULL THEN 'uncertain'
 WHEN expires_at_utc<=clock_timestamp() THEN 'rejected' ELSE 'queued' END,
code_envelope=CASE WHEN send_started_at_utc IS NOT NULL OR expires_at_utc<=clock_timestamp() THEN NULL ELSE code_envelope END,
lease_id=NULL,lease_until_utc=NULL,last_error_code='IFOOD_COMPLETION_RECOVERY_REQUIRED',
finished_at_utc=CASE WHEN send_started_at_utc IS NULL AND expires_at_utc<=clock_timestamp() THEN NOW() END
WHERE (state='processing' AND (lease_until_utc<=clock_timestamp() OR (send_started_at_utc IS NULL AND expires_at_utc<=clock_timestamp())))
 OR (state='queued' AND expires_at_utc<=clock_timestamp())
""", cancellationToken: ct));
        var ids = await c.QueryAsync<Guid>("SELECT operation_id FROM ifood_delivery_completions WHERE state='verified' ORDER BY created_at_utc LIMIT 20");
        foreach (var id in ids)
        {
            var opened = await LockOfficialOperation(id, ct); await using var connection = opened.Connection; await using var tx = opened.Transaction;
            if (opened.Operation?.State == "verified") await FinalizeOfficialCompletion(connection, tx, opened.Operation, ct);
            await tx.CommitAsync(ct);
        }
    }

    private static async Task FinalizeOfficialCompletion(NpgsqlConnection c, NpgsqlTransaction tx, OfficialCompletionOperation op, CancellationToken ct)
    {
        if (op.Kind == "takeout") { await FinalizeOfficialPickup(c, tx, op, ct); return; }
        var owned = await c.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM delivery_route_stops s JOIN pedido p ON p.id=s.pedido_id AND p.id_estabelecimento=s.estabelecimento_id
JOIN ifood_connections a ON a.merchant_id=@Merchant AND a.estabelecimento_id=s.estabelecimento_id
JOIN ifood_order_snapshots x ON x.merchant_id=@Merchant AND x.external_order_id=@Order AND x.estabelecimento_id=s.estabelecimento_id
WHERE s.id=@Stop AND s.estabelecimento_id=@Store AND s.motoboy_id=@Rider AND s.pedido_id=@Pedido AND s.stop_status='en_route'
AND p.motoboy_responsavel=@Rider AND s.picked_up_at_utc IS NOT NULL AND s.arrived_at_utc IS NOT NULL
AND a.status='authorized' AND a.authorized_at_utc IS NOT NULL AND a.revoked_at_utc IS NULL AND x.remote_status<>'CANCELLED'
AND x.data_is_current AND x.synchronization_state='synced' AND x.order_amount=@OrderTotal AND x.pending_amount=@Collect
AND (x.revision=@Revision OR x.remote_status='CONCLUDED') FOR SHARE OF a,x)
""", new { op.Merchant, op.Order, op.Stop, op.Store, op.Rider, op.Pedido, op.OrderTotal, op.Collect, op.Revision }, tx);
        if (!owned)
        {
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='review',last_error_code='IFOOD_COMPLETION_CONTEXT_CHANGED',code_envelope=NULL WHERE operation_id=@Operation", new { op.Operation }, tx);
            return;
        }
        var request = JsonSerializer.Deserialize<DeliveryCompletionRequest>(op.Request)!;
        await c.ExecuteAsync("UPDATE ifood_delivery_completions SET finalization_transaction_id=pg_current_xact_id() WHERE operation_id=@Operation", new { op.Operation }, tx);
        var manifest = JsonSerializer.Deserialize<DeliveryChecklist>(op.Manifest)!;
        // Pedido terminal tem snapshot ainda utilizável para conferência previamente persistida; não recalcular valores.
        if (request.Checklist != null) await SaveChecklistAsync(c, tx, op.Store, op.Rider, op.Stop, "delivery", manifest, request.Checklist, ct);
        if (op.Collect > 0) await c.ExecuteAsync("UPDATE pedido SET status_pagamento='pago' WHERE id=@Pedido AND id_estabelecimento=@Store", new { op.Pedido, op.Store }, tx);
        await CompleteCurrentInternalAsync(c, tx, op.Store, op.Rider, "motoboy", null, false, true);
        var name = await c.ExecuteScalarAsync<string?>("SELECT nome_cliente FROM pedido WHERE id=@Pedido", new { op.Pedido }, tx);
        var receipt = new DeliveryReceipt { OperationId = op.Operation, PedidoId = op.Pedido, NomeCliente = name,
            CompletedAtUtc = DateTimeOffset.UtcNow, Total = op.Collect, CodeChecked = op.CodeChecked, PaidBeforeDelivery = op.PaidBefore,
            Payments = request.Payments, ProofId = request.ProofId, Checklist = request.Checklist, Origin = "ifood",
            ExternalDisplayNumber = op.DisplayNumber, OfficialOrderTotal = op.OrderTotal, OfficialConfirmationEvidence = op.Evidence };
        await c.ExecuteAsync(new CommandDefinition("""
INSERT INTO delivery_completions(operation_id,estabelecimento_id,motoboy_id,user_id,session_id,session_epoch,pedido_id,stop_id,payload_hash,receipt)
VALUES(@Operation,@Store,@Rider,@User,@Session,@Epoch,@Pedido,@Stop,@Hash,CAST(@Receipt AS jsonb));
UPDATE ifood_delivery_completions SET state='succeeded',finished_at_utc=NOW(),code_envelope=NULL,last_error_code=NULL WHERE operation_id=@Operation
""", new { op.Operation, op.Store, op.Rider, op.User, op.Session, op.Epoch, op.Pedido, op.Stop, op.Hash, Receipt = JsonSerializer.Serialize(receipt) }, tx, cancellationToken: ct));
        if (await MotoboyWorkService.Available(c, tx, ct))
            await c.ExecuteAsync("UPDATE delivery_rider_work_entries SET store_cash=@Cash,cash_confirmed=TRUE WHERE stop_id=@Stop AND estabelecimento_id=@Store AND motoboy_id=@Rider",
                new { Cash = request.Payments.Where(p => p.Method == "dinheiro").Sum(p => p.Amount), op.Stop, op.Store, op.Rider }, tx);
    }

    internal static async Task<bool> ReconcileOfficialCompletion(NpgsqlConnection c, NpgsqlTransaction tx, Guid store, int pedido, string remote)
    {
        if (remote is not ("CANCELLED" or "CONCLUDED") || !await HasIfoodCompletionAsync(c, tx)) return false;
        if (remote == "CANCELLED")
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='review',last_error_code='IFOOD_COMPLETION_CANCELLED_AFTER_CONFIRMATION' WHERE estabelecimento_id=@Store AND pedido_id=@Pedido AND state='succeeded'", new { Store = store, Pedido = pedido }, tx);
        var op = await c.QuerySingleOrDefaultAsync<OfficialCompletionOperation>("SELECT " + OfficialOperationColumns + " FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND pedido_id=@Pedido AND state NOT IN('rejected','succeeded') FOR UPDATE", new { Store = store, Pedido = pedido }, tx);
        if (op == null) return false;
        if (remote == "CANCELLED")
        {
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='review',code_envelope=NULL,lease_id=NULL,lease_until_utc=NULL,last_error_code=CASE WHEN finished_at_utc IS NOT NULL THEN 'IFOOD_COMPLETION_CANCELLED_AFTER_CONFIRMATION' ELSE 'IFOOD_COMPLETION_CANCELLED' END WHERE operation_id=@Operation", new { op.Operation }, tx);
            return false;
        }
        var correlated = await c.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM ifood_delivery_completions o JOIN ifood_order_event_history h USING(merchant_id,external_order_id,estabelecimento_id)
WHERE o.operation_id=@Operation AND o.send_started_at_utc IS NOT NULL AND h.event_code='CONCLUDED' AND h.outcome='applied'
AND h.occurred_at_utc>=o.send_started_at_utc AND h.projection_transaction_id=pg_current_xact_id())
""", new { op.Operation }, tx);
        if (!correlated || op.State == "review")
        {
            await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='review',code_envelope=NULL,lease_id=NULL,lease_until_utc=NULL,last_error_code='IFOOD_COMPLETION_UNCORRELATED_EVENT' WHERE operation_id=@Operation", new { op.Operation }, tx);
            return false;
        }
        await c.ExecuteAsync("UPDATE ifood_delivery_completions SET state='verified',code_envelope=NULL,lease_id=NULL,lease_until_utc=NULL,evidence=COALESCE(evidence,'concluded_event') WHERE operation_id=@Operation", new { op.Operation }, tx);
        op.State = "verified"; op.Evidence ??= "concluded_event";
        await FinalizeOfficialCompletion(c, tx, op, default);
        return await c.ExecuteScalarAsync<bool>("SELECT state='succeeded' FROM ifood_delivery_completions WHERE operation_id=@Operation", new { op.Operation }, tx);
    }
}
