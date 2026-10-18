using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Events;

public interface IIfoodInboxRepository
{
    Task<IfoodPollingLease?> AcquirePollingAsync(string applicationKey, int leaseSeconds, CancellationToken ct);
    Task<DateTimeOffset> StartPollingRequestAsync(IfoodPollingLease lease, int leaseSeconds, CancellationToken ct);
    Task RenewPollingAsync(IfoodPollingLease lease, int leaseSeconds, CancellationToken ct);
    Task<DateTimeOffset> ReleasePollingAsync(IfoodPollingLease lease, TimeSpan delay, string? errorCode, CancellationToken ct);
    Task PersistAsync(IfoodPollingLease lease, IReadOnlyList<IfoodPollingMerchant> merchants, IReadOnlyList<IfoodEventEnvelope> events, CancellationToken ct);
    Task<IReadOnlyList<IfoodEventReceipt>> PendingAcknowledgmentsAsync(IfoodPollingLease lease, IReadOnlyList<IfoodPollingMerchant> merchants, int max, CancellationToken ct);
    Task AcknowledgedAsync(IfoodPollingLease lease, IReadOnlyList<IfoodEventReceipt> events, CancellationToken ct);
    Task AcknowledgmentFailedAsync(IfoodPollingLease lease, IReadOnlyList<IfoodEventReceipt> events, string code, TimeSpan delay, CancellationToken ct);
}

public enum IfoodInboxOutcome { Processed, Ignored, Review }

public sealed class IfoodInboxRepository : IIfoodInboxRepository
{
    private readonly NpgsqlDataSource _source;
    public IfoodInboxRepository(NpgsqlDataSource source) => _source = source;
    public static string ApplicationKey(string clientId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))).ToLowerInvariant();

    public Task<IfoodPollingLease?> AcquirePollingAsync(string applicationKey, int leaseSeconds, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_polling_state(application_key) VALUES(@Key) ON CONFLICT DO NOTHING
""", new { Key = applicationKey }, cancellationToken: ct));
        return await connection.QuerySingleOrDefaultAsync<IfoodPollingLease>(new CommandDefinition("""
UPDATE ifood_polling_state SET lease_id=@Lease,lease_until_utc=NOW()+make_interval(secs=>@Seconds),
    next_poll_at_utc=NOW()+INTERVAL '30 seconds'
WHERE application_key=@Key AND next_poll_at_utc<=NOW() AND (lease_id IS NULL OR lease_until_utc<=NOW())
RETURNING application_key AS ApplicationKey,lease_id AS LeaseId,next_poll_at_utc AS NextPollAtUtc
""", new { Key = applicationKey, Lease = Guid.NewGuid(), Seconds = leaseSeconds }, cancellationToken: ct));
    });

    public Task<DateTimeOffset> StartPollingRequestAsync(IfoodPollingLease lease, int leaseSeconds, CancellationToken ct)
        => TouchAsync(lease, leaseSeconds, true, ct);
    public async Task RenewPollingAsync(IfoodPollingLease lease, int leaseSeconds, CancellationToken ct)
        => _ = await TouchAsync(lease, leaseSeconds, false, ct);

    private Task<DateTimeOffset> TouchAsync(IfoodPollingLease lease, int seconds, bool sent, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var next = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition("""
UPDATE ifood_polling_state SET lease_until_utc=NOW()+make_interval(secs=>@Seconds),
    next_poll_at_utc=CASE WHEN @Sent THEN NOW()+INTERVAL '30 seconds' ELSE next_poll_at_utc END
WHERE application_key=@ApplicationKey AND lease_id=@LeaseId AND lease_until_utc>NOW()
RETURNING next_poll_at_utc
""", new { lease.ApplicationKey, lease.LeaseId, Seconds = seconds, Sent = sent }, cancellationToken: ct));
        return next ?? throw new IfoodIntegrationException("IFOOD_POLLING_LEASE_LOST");
    });

    public Task<DateTimeOffset> ReleasePollingAsync(IfoodPollingLease lease, TimeSpan delay, string? errorCode, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var next = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition("""
UPDATE ifood_polling_state SET lease_id=NULL,lease_until_utc=NULL,
    next_poll_at_utc=GREATEST(next_poll_at_utc,NOW()+make_interval(secs=>@Delay)),last_error_code=@Error
WHERE application_key=@ApplicationKey AND lease_id=@LeaseId AND lease_until_utc>NOW()
RETURNING next_poll_at_utc
""", new { lease.ApplicationKey, lease.LeaseId, Delay = Math.Max(0, delay.TotalSeconds), Error = errorCode }, cancellationToken: ct));
        return next ?? throw new IfoodIntegrationException("IFOOD_POLLING_LEASE_LOST");
    });

    public Task PersistAsync(IfoodPollingLease lease, IReadOnlyList<IfoodPollingMerchant> merchants,
        IReadOnlyList<IfoodEventEnvelope> events, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureLeaseAsync(connection, lease, transaction, ct);
        var mapping = merchants.ToDictionary(m => m.MerchantId, m => m.EstabelecimentoId);
        // Bloqueios somente durante gravação local; nenhum HTTP dentro da transação.
        foreach (var merchant in merchants.OrderBy(m => m.MerchantId))
        {
            var authorized = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
SELECT merchant_id FROM ifood_connections WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL FOR SHARE
""", merchant, transaction, cancellationToken: ct));
            if (authorized == null) throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
        }
        foreach (var item in events)
        {
            if (item.EventId == null || item.MerchantId == null || !mapping.TryGetValue(item.MerchantId.Value, out var store))
            {
                await RejectAsync(connection, transaction, lease, item, "IFOOD_EVENT_UNROUTABLE", ct);
                continue;
            }
            var id = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,external_order_id,event_code,payload,state,occurred_at_utc,ack_application_key)
VALUES(@MerchantId,@Store,@EventId,@OrderId,@Code,CAST(@PayloadJson AS jsonb),@State,@OccurredAtUtc,@ApplicationKey)
ON CONFLICT(merchant_id,event_id) DO UPDATE SET acknowledged_at_utc=NULL,ack_application_key=EXCLUDED.ack_application_key
WHERE ifood_events_inbox.payload=EXCLUDED.payload AND ifood_events_inbox.estabelecimento_id=EXCLUDED.estabelecimento_id
RETURNING id
""", new { item.MerchantId, Store = store, item.EventId, item.OrderId, item.Code, item.PayloadJson,
                State = item.RequiresReview ? "review" : "queued", item.OccurredAtUtc, lease.ApplicationKey }, transaction, cancellationToken: ct));
            if (id == null)
            {
                // Não sobrescrever evento já conhecido: preservar variante e exigir revisão.
                await RejectAsync(connection, transaction, lease, item, "IFOOD_EVENT_ID_COLLISION", ct);
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET acknowledged_at_utc=NULL,ack_application_key=@ApplicationKey,last_error_code='IFOOD_EVENT_ID_COLLISION',
    state=CASE WHEN state='processing' THEN state ELSE 'review' END
WHERE merchant_id=@MerchantId AND event_id=@EventId AND estabelecimento_id=@Store
""", new { item.MerchantId, item.EventId, Store = store, lease.ApplicationKey }, transaction, cancellationToken: ct));
            }
            else
            {
                // Marca na conexao da loja quando entrou o ultimo evento: e o sinal de que o polling esta vivo.
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET last_event_at_utc=GREATEST(COALESCE(last_event_at_utc,'-infinity'::timestamptz),COALESCE(@OccurredAtUtc,NOW()))
WHERE merchant_id=@MerchantId AND estabelecimento_id=@Store
""", new { item.MerchantId, Store = store, item.OccurredAtUtc }, transaction, cancellationToken: ct));
            }
        }
        await transaction.CommitAsync(ct);
        return true;
    });

    private static Task RejectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IfoodPollingLease lease,
        IfoodEventEnvelope item, string code, CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_event_rejections(application_key,payload_hash,payload,error_code)
VALUES(@ApplicationKey,@Hash,CAST(@PayloadJson AS jsonb),@Code) ON CONFLICT DO NOTHING
""", new { lease.ApplicationKey, Hash = ApplicationKey(item.PayloadJson), item.PayloadJson, Code = code }, transaction, cancellationToken: ct));

    public Task<IReadOnlyList<IfoodEventReceipt>> PendingAcknowledgmentsAsync(IfoodPollingLease lease,
        IReadOnlyList<IfoodPollingMerchant> merchants, int max, CancellationToken ct) => Guard<IReadOnlyList<IfoodEventReceipt>>(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await EnsureLeaseAsync(connection, lease, null, ct);
        var rows = await connection.QueryAsync<IfoodEventReceipt>(new CommandDefinition("""
SELECT e.merchant_id AS MerchantId,e.estabelecimento_id AS EstabelecimentoId,e.event_id AS EventId
FROM ifood_events_inbox e JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
WHERE e.merchant_id=ANY(@Merchants) AND e.acknowledged_at_utc IS NULL AND e.ack_next_attempt_at_utc<=NOW()
AND e.ack_application_key=@ApplicationKey
AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
ORDER BY e.received_at_utc,e.id LIMIT @Max
""", new { Merchants = merchants.Select(m => m.MerchantId).ToArray(), Max = max, lease.ApplicationKey }, cancellationToken: ct));
        var mapping = merchants.ToDictionary(m => m.MerchantId, m => m.EstabelecimentoId);
        return rows.Where(e => mapping.TryGetValue(e.MerchantId, out var store) && store == e.EstabelecimentoId).ToArray();
    });

    public Task AcknowledgedAsync(IfoodPollingLease lease, IReadOnlyList<IfoodEventReceipt> events, CancellationToken ct)
        => UpdateAcknowledgmentsAsync(lease, events, null, TimeSpan.Zero, ct);
    public Task AcknowledgmentFailedAsync(IfoodPollingLease lease, IReadOnlyList<IfoodEventReceipt> events, string code, TimeSpan delay, CancellationToken ct)
        => UpdateAcknowledgmentsAsync(lease, events, code, delay, ct);

    private Task UpdateAcknowledgmentsAsync(IfoodPollingLease lease, IReadOnlyList<IfoodEventReceipt> events, string? code, TimeSpan delay, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureLeaseAsync(connection, lease, transaction, ct);
        foreach (var item in events)
            await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET acknowledged_at_utc=CASE WHEN @Code IS NULL THEN NOW() ELSE NULL END,
ack_attempts=CASE WHEN @Code IS NULL THEN 0 ELSE ack_attempts+1 END,ack_last_error_code=@Code,
ack_next_attempt_at_utc=NOW()+make_interval(secs=>CASE WHEN @Code IS NULL THEN 0 ELSE GREATEST(@Delay,LEAST(300,30*power(2,LEAST(4,ack_attempts)))) END)
WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId AND event_id=@EventId AND ack_application_key=@ApplicationKey
""", new { item.MerchantId, item.EstabelecimentoId, item.EventId, lease.ApplicationKey, Code = code, Delay = Math.Max(0, delay.TotalSeconds) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    });

    private static async Task EnsureLeaseAsync(NpgsqlConnection connection, IfoodPollingLease lease, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var key = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
SELECT application_key FROM ifood_polling_state
WHERE application_key=@ApplicationKey AND lease_id=@LeaseId AND lease_until_utc>NOW() FOR SHARE
""", lease, transaction, cancellationToken: ct));
        if (key == null) throw new IfoodIntegrationException("IFOOD_POLLING_LEASE_LOST");
    }

    // Primitivas para IF-07/IF-08: nenhum worker marca evento processado sem efeito de domínio.
    public Task<IReadOnlyList<IfoodInboxReservation>> ClaimAsync(int max, int leaseSeconds, int maxAttempts, CancellationToken ct = default,
        string[]? codes = null, string[]? excludedCodes = null, bool importCandidates = false, bool requireOrder = false) => Guard<IReadOnlyList<IfoodInboxReservation>>(async () =>
    {
        if (max is < 1 or > 2000 || leaseSeconds is < 1 or > 600 || maxAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(max));
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET state=CASE WHEN attempts>=@MaxAttempts THEN 'failed' ELSE 'queued' END,
    lease_id=NULL,lease_until_utc=NULL,last_error_code='IFOOD_EVENT_LEASE_EXPIRED'
WHERE state='processing' AND lease_until_utc<=NOW()
AND (CAST(@Codes AS text[]) IS NULL OR event_code=ANY(CAST(@Codes AS text[])))
AND (CAST(@ExcludedCodes AS text[]) IS NULL OR NOT event_code=ANY(CAST(@ExcludedCodes AS text[])))
""", new { MaxAttempts = maxAttempts, Codes = codes, ExcludedCodes = excludedCodes }, transaction, cancellationToken: ct));
        var rows = await connection.QueryAsync<IfoodInboxReservation>(new CommandDefinition("""
WITH picked AS (
    SELECT e.id FROM ifood_events_inbox e JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
    WHERE e.state='queued' AND e.next_attempt_at_utc<=NOW() AND e.attempts<@MaxAttempts
    AND (CAST(@Codes AS text[]) IS NULL OR e.event_code=ANY(CAST(@Codes AS text[])))
    AND (CAST(@ExcludedCodes AS text[]) IS NULL OR NOT e.event_code=ANY(CAST(@ExcludedCodes AS text[])))
    AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
    AND (NOT @ImportCandidates OR e.event_code IN ('PLC','PLACED') OR (e.external_order_id IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM ifood_order_snapshots sn WHERE sn.merchant_id=e.merchant_id AND sn.external_order_id=e.external_order_id AND sn.estabelecimento_id=e.estabelecimento_id)))
    AND (NOT @RequireOrder OR e.external_order_id IS NULL OR EXISTS
        (SELECT 1 FROM ifood_order_snapshots sn WHERE sn.merchant_id=e.merchant_id AND sn.external_order_id=e.external_order_id AND sn.estabelecimento_id=e.estabelecimento_id))
    ORDER BY e.occurred_at_utc NULLS LAST,e.received_at_utc,e.id LIMIT @Max FOR UPDATE OF e SKIP LOCKED
)
UPDATE ifood_events_inbox e SET state='processing',attempts=e.attempts+1,
    lease_id=@Lease,lease_until_utc=NOW()+make_interval(secs=>@Seconds)
FROM picked WHERE e.id=picked.id
RETURNING e.id AS Id,e.merchant_id AS MerchantId,e.estabelecimento_id AS EstabelecimentoId,e.event_id AS EventId,
    e.external_order_id AS ExternalOrderId,e.event_code AS EventCode,e.payload::text AS PayloadJson,e.attempts AS Attempts,e.lease_id AS LeaseId,e.occurred_at_utc AS OccurredAtUtc
""", new { Max = max, MaxAttempts = maxAttempts, Lease = Guid.NewGuid(), Seconds = leaseSeconds, Codes = codes, ExcludedCodes = excludedCodes, ImportCandidates = importCandidates, RequireOrder = requireOrder }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return rows.ToArray();
    });

    public Task<bool> RenewEventAsync(IfoodInboxReservation item, int seconds, CancellationToken ct = default) => Guard(async () =>
    {
        if (seconds is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET lease_until_utc=NOW()+make_interval(secs=>@Seconds)
WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId AND state='processing'
AND lease_id=@LeaseId AND lease_until_utc>NOW()
""", new { item.Id, item.MerchantId, item.EstabelecimentoId, item.LeaseId, Seconds = seconds }, cancellationToken: ct)) == 1;
    });

    public Task<bool> FinishEventAsync(IfoodInboxReservation item, IfoodInboxOutcome outcome, CancellationToken ct = default, string? errorCode = null)
    {
        var state = outcome switch { IfoodInboxOutcome.Processed => "processed", IfoodInboxOutcome.Ignored => "ignored",
            IfoodInboxOutcome.Review => "review", _ => throw new ArgumentOutOfRangeException(nameof(outcome)) };
        return EndEventAsync(item, state, errorCode, TimeSpan.Zero, 0, ct);
    }
    public Task<bool> RetryEventAsync(IfoodInboxReservation item, int maxAttempts, TimeSpan delay, CancellationToken ct = default)
    {
        if (maxAttempts is < 1 or > 100 || delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        return EndEventAsync(item, "retry", "IFOOD_EVENT_RETRY", delay, maxAttempts, ct);
    }
    private Task<bool> EndEventAsync(IfoodInboxReservation item, string state, string? error, TimeSpan delay, int maxAttempts, CancellationToken ct) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_events_inbox SET state=CASE WHEN @State='retry' THEN CASE WHEN attempts>=@MaxAttempts THEN 'failed' ELSE 'queued' END ELSE @State END,
    processed_at_utc=CASE WHEN @State='processed' OR @State='ignored' THEN NOW() ELSE NULL END,
    lease_id=NULL,lease_until_utc=NULL,last_error_code=@Error,next_attempt_at_utc=NOW()+make_interval(secs=>@Delay)
WHERE id=@Id AND merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId AND state='processing'
AND lease_id=@LeaseId AND lease_until_utc>NOW()
""", new { item.Id, item.MerchantId, item.EstabelecimentoId, item.LeaseId, State = state, Error = error,
            Delay = delay.TotalSeconds, MaxAttempts = maxAttempts }, cancellationToken: ct)) == 1;
    });

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { throw new IfoodIntegrationException("IFOOD_INBOX_DATABASE_ERROR", retryable: true); }
    }
}
