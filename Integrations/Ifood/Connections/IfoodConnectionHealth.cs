using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Connections;

/// <summary>Saude do polling por merchant: quem o iFood recusa fica em quarentena sem parar os demais.</summary>
public interface IIfoodConnectionHealth
{
    /// <summary>Merchants ativos em quarentena e ate quando; os demais estao saudaveis.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> BlockedAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default);
    Task RecordPollSuccessAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default);
    /// <summary>Isola o merchant e agenda uma nova tentativa isolada (60s, 2min, 4min... ate 1h).</summary>
    Task RecordPollFailureAsync(Guid merchant, string code, CancellationToken ct = default);
    Task RecordRemoteCheckAsync(Guid merchant, string code, CancellationToken ct = default);
    /// <summary>Faz o proximo ciclo tentar este merchant agora (revalidacao pedida por uma pessoa).</summary>
    Task<bool> RetryNowAsync(Guid merchant, CancellationToken ct = default);
}

public sealed class NullIfoodConnectionHealth : IIfoodConnectionHealth
{
    public static readonly NullIfoodConnectionHealth Instance = new();
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> BlockedAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>());
    public Task RecordPollSuccessAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordPollFailureAsync(Guid merchant, string code, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordRemoteCheckAsync(Guid merchant, string code, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RetryNowAsync(Guid merchant, CancellationToken ct = default) => Task.FromResult(false);
}

public sealed class IfoodConnectionHealth : IIfoodConnectionHealth
{
    private readonly NpgsqlDataSource _source;
    public IfoodConnectionHealth(NpgsqlDataSource source) => _source = source;

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> BlockedAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default) => Guard<IReadOnlyDictionary<Guid, DateTimeOffset>>(async () =>
    {
        if (merchants.Count == 0) return new Dictionary<Guid, DateTimeOffset>();
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid MerchantId, DateTimeOffset Until)>(new CommandDefinition("""
SELECT merchant_id AS MerchantId, poll_blocked_until_utc AS Until FROM ifood_connections
WHERE merchant_id=ANY(@Merchants) AND revoked_at_utc IS NULL AND poll_blocked_until_utc IS NOT NULL
""", new { Merchants = merchants.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.MerchantId, r => r.Until);
    });

    public Task RecordPollSuccessAsync(IReadOnlyCollection<Guid> merchants, CancellationToken ct = default) => Guard(async () =>
    {
        if (merchants.Count == 0) return true;
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET poll_state='ok', poll_error_code=NULL, poll_failures=0, poll_blocked_until_utc=NULL,
    last_poll_at_utc=NOW(), last_poll_ok_at_utc=NOW()
WHERE merchant_id=ANY(@Merchants) AND revoked_at_utc IS NULL
""", new { Merchants = merchants.ToArray() }, cancellationToken: ct));
        return true;
    });

    public Task RecordPollFailureAsync(Guid merchant, string code, CancellationToken ct = default) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET poll_state=CASE WHEN @Code='IFOOD_EVENTS_FORBIDDEN' OR @Code='IFOOD_MERCHANT_NOT_AUTHORIZED' THEN 'forbidden' ELSE 'error' END,
    poll_error_code=@Code, last_poll_at_utc=NOW(),
    poll_blocked_until_utc=NOW()+make_interval(secs=>LEAST(3600, 60*power(2, LEAST(poll_failures, 6)))),
    poll_failures=poll_failures+1
WHERE merchant_id=@Merchant AND revoked_at_utc IS NULL
""", new { Merchant = merchant, Code = code }, cancellationToken: ct));
        return true;
    });

    public Task RecordRemoteCheckAsync(Guid merchant, string code, CancellationToken ct = default) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET remote_check_code=@Code, remote_checked_at_utc=NOW()
WHERE merchant_id=@Merchant AND revoked_at_utc IS NULL
""", new { Merchant = merchant, Code = code }, cancellationToken: ct));
        return true;
    });

    public Task<bool> RetryNowAsync(Guid merchant, CancellationToken ct = default) => Guard(async () =>
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET poll_blocked_until_utc=CASE WHEN poll_blocked_until_utc IS NULL THEN NULL ELSE NOW() END, poll_failures=0
WHERE merchant_id=@Merchant AND revoked_at_utc IS NULL AND status='authorized'
""", new { Merchant = merchant }, cancellationToken: ct)) > 0;
    });

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { throw new IfoodIntegrationException("IFOOD_CONNECTION_UNAVAILABLE", retryable: true); }
    }
}
