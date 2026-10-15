using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Reconciliation;

public sealed class IfoodOrderEventRow
{
    public long InboxId { get; set; }
    public string EventCode { get; set; } = "";
    public string State { get; set; } = "";
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? Outcome { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class IfoodAlertRow
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public int Count { get; set; }
    public DateTimeOffset? OldestAtUtc { get; set; }
}

public sealed class IfoodPollingHealth
{
    public DateTimeOffset? NextPollAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
}

public interface IIfoodReconciliationRepository
{
    // Eventos do pedido (sem payload), sempre limitados à loja informada.
    Task<IReadOnlyList<IfoodOrderEventRow>> ListOrderEventsAsync(Guid estabelecimentoId, int pedidoId, CancellationToken ct = default);
    // Repete a projeção de um evento em revisão/falha com o código atual. Não altera o pedido por si só.
    Task RequeueEventAsync(Guid estabelecimentoId, int pedidoId, long inboxId, int actorUserId, CancellationToken ct = default);
    // Marca como tratado um evento que o operador conferiu no iFood. Exige anotação e fica auditado.
    Task DismissEventAsync(Guid estabelecimentoId, int pedidoId, long inboxId, int actorUserId, string note, CancellationToken ct = default);
    // Devolve o pedido de "revisão" para "sincronizado" depois de conferido. Recusa com evento ou comando pendente.
    Task ResolveReviewAsync(Guid estabelecimentoId, int pedidoId, int actorUserId, string note, CancellationToken ct = default);
    Task<IReadOnlyList<IfoodAlertRow>> GetStoreAlertsAsync(Guid estabelecimentoId, CancellationToken ct = default);
    Task<IfoodPollingHealth?> GetPollingHealthAsync(CancellationToken ct = default);
}

public sealed class IfoodReconciliationRepository : IIfoodReconciliationRepository
{
    private const int MinNoteLength = 5;
    private readonly NpgsqlDataSource _source;
    public IfoodReconciliationRepository(NpgsqlDataSource source) => _source = source;

    public async Task<IReadOnlyList<IfoodOrderEventRow>> ListOrderEventsAsync(Guid estabelecimentoId, int pedidoId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<IfoodOrderEventRow>(new CommandDefinition("""
SELECT i.id AS InboxId, i.event_code AS EventCode, i.state AS State, i.last_error_code AS LastErrorCode,
       i.occurred_at_utc AS OccurredAtUtc, i.received_at_utc AS ReceivedAtUtc, i.processed_at_utc AS ProcessedAtUtc,
       h.outcome AS Outcome, h.reason_code AS ReasonCode
FROM ifood_order_links l
JOIN ifood_events_inbox i ON i.merchant_id=l.merchant_id AND i.external_order_id=l.external_order_id
LEFT JOIN ifood_order_event_history h ON h.inbox_id=i.id
WHERE l.pedido_id=@Pedido AND l.estabelecimento_id=@Store
ORDER BY COALESCE(i.occurred_at_utc, i.received_at_utc), i.id
""", new { Pedido = pedidoId, Store = estabelecimentoId }, cancellationToken: ct));
        return rows.AsList();
    }

    private sealed class LockedEvent
    {
        public long Id { get; set; }
        public string State { get; set; } = "";
        public Guid MerchantId { get; set; }
        public Guid ExternalOrderId { get; set; }
        public bool HasPayload { get; set; }
    }

    private static async Task<LockedEvent> LockEventAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid store, int pedido, long inboxId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<LockedEvent>(new CommandDefinition("""
SELECT i.id AS Id, i.state AS State, i.merchant_id AS MerchantId, i.external_order_id AS ExternalOrderId,
       (i.payload <> '{}'::jsonb) AS HasPayload
FROM ifood_events_inbox i
JOIN ifood_order_links l ON l.merchant_id=i.merchant_id AND l.external_order_id=i.external_order_id
WHERE i.id=@Id AND l.pedido_id=@Pedido AND l.estabelecimento_id=@Store
FOR UPDATE OF i
""", new { Id = inboxId, Pedido = pedido, Store = store }, tx, cancellationToken: ct));
        if (row == null) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_NOT_FOUND");
        if (row.State is not ("review" or "failed")) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_INVALID_STATE");
        return row;
    }

    private static Task Audit(NpgsqlConnection connection, NpgsqlTransaction tx, Guid store, Guid merchant, Guid order, long? inboxId,
        string action, int actor, string? note, string? previousState, CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_reconciliation_audit(merchant_id,estabelecimento_id,external_order_id,inbox_id,action,actor_user_id,note,previous_state)
VALUES(@Merchant,@Store,@Order,@Inbox,@Action,@Actor,@Note,@Previous)
""", new { Merchant = merchant, Store = store, Order = order, Inbox = inboxId, Action = action, Actor = actor, Note = note, Previous = previousState }, tx, cancellationToken: ct));

    private static string RequireNote(string? note)
    {
        var text = note?.Trim() ?? "";
        if (text.Length < MinNoteLength || text.Length > 500) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_NOTE_REQUIRED");
        return text;
    }

    public async Task RequeueEventAsync(Guid estabelecimentoId, int pedidoId, long inboxId, int actorUserId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LockEventAsync(connection, tx, estabelecimentoId, pedidoId, inboxId, ct);
        // Eventos além da retenção tiveram o payload apagado e não podem ser reprocessados.
        if (!row.HasPayload) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_PAYLOAD_EXPIRED");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM ifood_order_event_history WHERE inbox_id=@Id", new { Id = inboxId }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET state='queued', attempts=0, last_error_code=NULL, lease_id=NULL, lease_until_utc=NULL,
       next_attempt_at_utc=NOW(), processed_at_utc=NULL
WHERE id=@Id
""", new { Id = inboxId }, tx, cancellationToken: ct));
        await Audit(connection, tx, estabelecimentoId, row.MerchantId, row.ExternalOrderId, inboxId, "requeue", actorUserId, null, row.State, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DismissEventAsync(Guid estabelecimentoId, int pedidoId, long inboxId, int actorUserId, string note, CancellationToken ct = default)
    {
        var text = RequireNote(note);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LockEventAsync(connection, tx, estabelecimentoId, pedidoId, inboxId, ct);
        // O erro original permanece em last_error_code e no histórico; só o estado muda.
        await connection.ExecuteAsync(new CommandDefinition("UPDATE ifood_events_inbox SET state='ignored', processed_at_utc=NOW() WHERE id=@Id",
            new { Id = inboxId }, tx, cancellationToken: ct));
        await Audit(connection, tx, estabelecimentoId, row.MerchantId, row.ExternalOrderId, inboxId, "dismiss", actorUserId, text, row.State, ct);
        await tx.CommitAsync(ct);
    }

    private sealed class LockedSnapshot
    {
        public Guid MerchantId { get; set; }
        public Guid ExternalOrderId { get; set; }
        public string SynchronizationState { get; set; } = "";
        public bool DataIsCurrent { get; set; }
    }

    public async Task ResolveReviewAsync(Guid estabelecimentoId, int pedidoId, int actorUserId, string note, CancellationToken ct = default)
    {
        var text = RequireNote(note);
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var snapshot = await connection.QuerySingleOrDefaultAsync<LockedSnapshot>(new CommandDefinition("""
SELECT s.merchant_id AS MerchantId, s.external_order_id AS ExternalOrderId,
       s.synchronization_state AS SynchronizationState, s.data_is_current AS DataIsCurrent
FROM ifood_order_links l
JOIN ifood_order_snapshots s ON s.merchant_id=l.merchant_id AND s.external_order_id=l.external_order_id
WHERE l.pedido_id=@Pedido AND l.estabelecimento_id=@Store
FOR UPDATE OF s
""", new { Pedido = pedidoId, Store = estabelecimentoId }, tx, cancellationToken: ct));
        if (snapshot == null) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_NOT_FOUND");
        if (snapshot.SynchronizationState != "review") throw new IfoodIntegrationException("IFOOD_RECONCILIATION_INVALID_STATE");
        // Um patch incompleto deixou valores sem conferência: o pedido precisa ser reimportado, não apenas liberado.
        if (!snapshot.DataIsCurrent) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_DATA_STALE");
        var pendingEvents = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
SELECT COUNT(*)::int FROM ifood_events_inbox
WHERE merchant_id=@Merchant AND external_order_id=@Order AND state IN ('queued','processing','review','failed')
""", new { Merchant = snapshot.MerchantId, Order = snapshot.ExternalOrderId }, tx, cancellationToken: ct));
        if (pendingEvents > 0) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_EVENTS_PENDING");
        var pendingCommands = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
SELECT COUNT(*)::int FROM ifood_outbound_commands
WHERE merchant_id=@Merchant AND external_order_id=@Order AND state IN ('queued','processing','uncertain','review')
""", new { Merchant = snapshot.MerchantId, Order = snapshot.ExternalOrderId }, tx, cancellationToken: ct));
        if (pendingCommands > 0) throw new IfoodIntegrationException("IFOOD_RECONCILIATION_COMMAND_PENDING");
        // A revisão sobe para invalidar telas abertas com a versão anterior do pedido.
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_snapshots SET synchronization_state='synced', revision=revision+1, updated_at_utc=NOW()
WHERE merchant_id=@Merchant AND external_order_id=@Order
""", new { Merchant = snapshot.MerchantId, Order = snapshot.ExternalOrderId }, tx, cancellationToken: ct));
        await Audit(connection, tx, estabelecimentoId, snapshot.MerchantId, snapshot.ExternalOrderId, null, "resolve_review", actorUserId, text, "review", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<IfoodAlertRow>> GetStoreAlertsAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        // Aceite: o iFood dá 8 minutos; avisa aos 3 e fica crítico aos 6.
        var rows = (await connection.QueryAsync<IfoodAlertRow>(new CommandDefinition("""
WITH waiting AS (
    SELECT COALESCE(CASE WHEN s.payload->>'createdAt' ~ '^\d{4}-\d{2}-\d{2}T.*(Z|[+-]\d{2}:\d{2})$'
                         THEN (s.payload->>'createdAt')::timestamptz END, l.created_at_utc) AS created_at
    FROM ifood_order_snapshots s
    JOIN ifood_order_links l ON l.merchant_id=s.merchant_id AND l.external_order_id=s.external_order_id
    WHERE s.estabelecimento_id=@Store AND s.preparation_state='awaiting_acceptance'
      AND COALESCE(s.remote_status,'PLACED')='PLACED' AND s.order_type IN ('DELIVERY','TAKEOUT')
)
SELECT 'IFOOD_ACCEPTANCE_DEADLINE' AS Code, 'critical' AS Severity, COUNT(*)::int AS Count, MIN(created_at) AS OldestAtUtc
FROM waiting WHERE created_at <= NOW() - INTERVAL '6 minutes'
UNION ALL
SELECT 'IFOOD_ACCEPTANCE_WAITING', 'warning', COUNT(*)::int, MIN(created_at)
FROM waiting WHERE created_at <= NOW() - INTERVAL '3 minutes' AND created_at > NOW() - INTERVAL '6 minutes'
UNION ALL
SELECT 'IFOOD_ORDER_REVIEW', 'warning', COUNT(*)::int, MIN(updated_at_utc)
FROM ifood_order_snapshots WHERE estabelecimento_id=@Store AND synchronization_state='review' AND updated_at_utc > NOW() - INTERVAL '30 days'
UNION ALL
SELECT 'IFOOD_EVENT_BACKLOG', 'warning', COUNT(*)::int, MIN(received_at_utc)
FROM ifood_events_inbox WHERE estabelecimento_id=@Store AND state IN ('queued','processing') AND received_at_utc <= NOW() - INTERVAL '3 minutes'
UNION ALL
SELECT 'IFOOD_EVENT_FAILED', 'critical', COUNT(*)::int, MIN(received_at_utc)
FROM ifood_events_inbox WHERE estabelecimento_id=@Store AND state='failed' AND received_at_utc > NOW() - INTERVAL '7 days'
UNION ALL
SELECT 'IFOOD_COMMAND_UNCERTAIN', 'critical', COUNT(*)::int, MIN(updated_at_utc)
FROM ifood_outbound_commands WHERE estabelecimento_id=@Store AND state IN ('uncertain','review') AND updated_at_utc > NOW() - INTERVAL '7 days'
UNION ALL
SELECT 'IFOOD_COMPLETION_UNCERTAIN', 'critical', COUNT(*)::int, MIN(created_at_utc)
FROM ifood_delivery_completions WHERE estabelecimento_id=@Store AND state IN ('uncertain','review') AND created_at_utc > NOW() - INTERVAL '7 days'
""", new { Store = estabelecimentoId }, cancellationToken: ct))).AsList();
        return rows.Where(row => row.Count > 0).ToList();
    }

    public async Task<IfoodPollingHealth?> GetPollingHealthAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<IfoodPollingHealth>(new CommandDefinition(
            "SELECT next_poll_at_utc AS NextPollAtUtc, last_error_code AS LastErrorCode FROM ifood_polling_state ORDER BY next_poll_at_utc DESC LIMIT 1",
            cancellationToken: ct));
    }
}
