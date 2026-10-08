using APIBack.Model.Tracking;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class DeliveryOutboxRepository(NpgsqlDataSource dataSource) : IDeliveryOutboxRepository
{
    public async Task<IReadOnlyList<DeliveryOutboxRecord>?> ClaimAsync(
        Guid leaseId, int leaseSeconds, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var claimed = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO delivery_outbox_publisher_lease(singleton, lease_id, expires_at_utc)
VALUES (TRUE, @LeaseId, NOW() + (@Seconds * INTERVAL '1 second'))
ON CONFLICT(singleton) DO UPDATE
 SET lease_id = EXCLUDED.lease_id, expires_at_utc = EXCLUDED.expires_at_utc
 WHERE delivery_outbox_publisher_lease.expires_at_utc <= NOW()
RETURNING lease_id;", new { LeaseId = leaseId, Seconds = leaseSeconds }, transaction,
            commandTimeout: 10, cancellationToken: cancellationToken));
        if (!claimed.HasValue) { await transaction.CommitAsync(cancellationToken); return null; }
        var records = (await connection.QueryAsync<DeliveryOutboxRecord>(new CommandDefinition(@"
SELECT o.event_id AS EventId, o.event_name AS EventName, o.target_group AS TargetGroup,
       o.payload::text AS Payload, o.occurred_at_utc AS OccurredAtUtc, o.motoboy_id AS MotoboyId
  FROM delivery_realtime_outbox o
 WHERE o.published_at_utc IS NULL AND o.next_attempt_at_utc <= NOW()
   AND NOT EXISTS (
       SELECT 1 FROM delivery_realtime_outbox earlier
        WHERE earlier.published_at_utc IS NULL AND earlier.next_attempt_at_utc > NOW()
          AND earlier.target_group = o.target_group
          AND earlier.motoboy_id IS NOT DISTINCT FROM o.motoboy_id
          AND (earlier.occurred_at_utc, earlier.event_id) < (o.occurred_at_utc, o.event_id))
 ORDER BY o.occurred_at_utc, o.event_id LIMIT @Limit;",
            new { Limit = limit }, transaction, commandTimeout: 10, cancellationToken: cancellationToken))).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    public async Task<bool> RenewAsync(Guid leaseId, int leaseSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE delivery_outbox_publisher_lease SET expires_at_utc = NOW() + (@Seconds * INTERVAL '1 second')
 WHERE singleton = TRUE AND lease_id = @LeaseId AND expires_at_utc > NOW();",
            new { LeaseId = leaseId, Seconds = leaseSeconds }, commandTimeout: 10, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> CompleteAsync(Guid leaseId, Guid eventId, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Bloquear so durante o ACK: impede takeover entre validar a reserva e confirmar.
        var ownsLease = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(@"
SELECT TRUE FROM delivery_outbox_publisher_lease
 WHERE singleton = TRUE AND lease_id = @LeaseId AND expires_at_utc > NOW() FOR UPDATE;",
            new { LeaseId = leaseId }, transaction, commandTimeout: 10, cancellationToken: cancellationToken));
        if (ownsLease != true) { await transaction.CommitAsync(cancellationToken); return false; }
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE delivery_realtime_outbox
 SET published_at_utc = CASE WHEN @Error IS NULL THEN NOW() ELSE NULL END,
     attempts = attempts + 1, last_error = LEFT(@Error, 2000),
     next_attempt_at_utc = CASE WHEN @Error IS NULL THEN next_attempt_at_utc
         ELSE NOW() + (LEAST(attempts + 1, 60) * INTERVAL '1 second') END
 WHERE event_id = @EventId AND published_at_utc IS NULL;",
            new { EventId = eventId, Error = error }, transaction, commandTimeout: 10, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE delivery_outbox_publisher_lease SET expires_at_utc = NOW()
 WHERE singleton = TRUE AND lease_id = @LeaseId;", new { LeaseId = leaseId },
            commandTimeout: 10, cancellationToken: cancellationToken));
    }
}
