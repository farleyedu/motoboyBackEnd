using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Config;

public interface IIfoodRuntimeSettingsRepository
{
    Task<IfoodRuntimeSettings> GetSettingsAsync(CancellationToken ct = default);
    Task UpdateSettingsAsync(string clientId, string? clientSecretCifrado, bool enabled, bool pollingEnabled,
        bool importEnabled, bool projectionEnabled, bool commandsEnabled, bool exceptionsEnabled, string? updatedBy, CancellationToken ct = default);
    Task<IReadOnlyList<IfoodConnectionRow>> ListConnectionsAsync(CancellationToken ct = default);
    // Conexão mais recente de UMA loja; nunca devolve dados de outro estabelecimento.
    Task<IfoodConnectionRow?> GetStoreConnectionAsync(Guid estabelecimentoId, CancellationToken ct = default);
    Task AuthorizeConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default);
    Task RevokeConnectionAsync(Guid merchantId, CancellationToken ct = default);
}

public sealed class IfoodRuntimeSettingsRepository : IIfoodRuntimeSettingsRepository
{
    private readonly NpgsqlDataSource _source;
    public IfoodRuntimeSettingsRepository(NpgsqlDataSource source) => _source = source;

    public async Task<IfoodRuntimeSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<IfoodRuntimeSettings>(new CommandDefinition("""
SELECT client_id AS ClientId, client_secret_cifrado AS ClientSecretCifrado, enabled AS Enabled,
       polling_enabled AS PollingEnabled, import_enabled AS ImportEnabled, projection_enabled AS ProjectionEnabled,
       commands_enabled AS CommandsEnabled, exceptions_enabled AS ExceptionsEnabled,
       updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy
FROM ifood_app_settings WHERE id=1
""", cancellationToken: ct));
        return row ?? new IfoodRuntimeSettings();
    }

    public async Task UpdateSettingsAsync(string clientId, string? clientSecretCifrado, bool enabled, bool pollingEnabled,
        bool importEnabled, bool projectionEnabled, bool commandsEnabled, bool exceptionsEnabled, string? updatedBy, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        // clientSecretCifrado nulo preserva o segredo já gravado (toggle isolado não exige redigitar).
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_app_settings SET client_id=@ClientId,
    client_secret_cifrado=COALESCE(@ClientSecretCifrado, client_secret_cifrado),
    enabled=@Enabled, polling_enabled=@PollingEnabled, import_enabled=@ImportEnabled,
    projection_enabled=@ProjectionEnabled, commands_enabled=@CommandsEnabled, exceptions_enabled=@ExceptionsEnabled,
    updated_at_utc=NOW(), updated_by=@UpdatedBy
WHERE id=1
""", new { ClientId = clientId, ClientSecretCifrado = clientSecretCifrado, Enabled = enabled, PollingEnabled = pollingEnabled,
            ImportEnabled = importEnabled, ProjectionEnabled = projectionEnabled, CommandsEnabled = commandsEnabled,
            ExceptionsEnabled = exceptionsEnabled, UpdatedBy = updatedBy }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IfoodConnectionRow>> ListConnectionsAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<IfoodConnectionRow>(new CommandDefinition("""
SELECT merchant_id AS MerchantId, estabelecimento_id AS EstabelecimentoId, status AS Status,
       authorized_at_utc AS AuthorizedAtUtc, revoked_at_utc AS RevokedAtUtc, last_event_at_utc AS LastEventAtUtc,
       created_at_utc AS CreatedAtUtc
FROM ifood_connections ORDER BY created_at_utc DESC
""", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IfoodConnectionRow?> GetStoreConnectionAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (estabelecimentoId == Guid.Empty) return null;
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<IfoodConnectionRow>(new CommandDefinition("""
SELECT merchant_id AS MerchantId, estabelecimento_id AS EstabelecimentoId, status AS Status,
       authorized_at_utc AS AuthorizedAtUtc, revoked_at_utc AS RevokedAtUtc, last_event_at_utc AS LastEventAtUtc,
       created_at_utc AS CreatedAtUtc
FROM ifood_connections WHERE estabelecimento_id=@EstabelecimentoId
ORDER BY (revoked_at_utc IS NULL) DESC, created_at_utc DESC LIMIT 1
""", new { EstabelecimentoId = estabelecimentoId }, cancellationToken: ct));
    }

    public async Task AuthorizeConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (merchantId == Guid.Empty || estabelecimentoId == Guid.Empty)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT estabelecimento_id FROM ifood_connections WHERE merchant_id=@MerchantId FOR UPDATE",
            new { MerchantId = merchantId }, tx, cancellationToken: ct));
        if (existing is { } current && current != estabelecimentoId)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE");
        await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connections(merchant_id, estabelecimento_id, status, authorized_at_utc, revoked_at_utc)
VALUES (@MerchantId, @EstabelecimentoId, 'authorized', NOW(), NULL)
ON CONFLICT (merchant_id) DO UPDATE SET status='authorized', authorized_at_utc=NOW(), revoked_at_utc=NULL, updated_at_utc=NOW()
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task RevokeConnectionAsync(Guid merchantId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='revoked', revoked_at_utc=NOW(), updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND revoked_at_utc IS NULL
""", new { MerchantId = merchantId }, cancellationToken: ct));
        if (affected == 0) throw new IfoodIntegrationException("IFOOD_CONNECTION_NOT_FOUND");
    }
}
