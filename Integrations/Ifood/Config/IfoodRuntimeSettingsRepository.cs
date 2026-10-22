using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Config;

public interface IIfoodRuntimeSettingsRepository
{
    Task<IfoodRuntimeSettings> GetSettingsAsync(CancellationToken ct = default);
    Task UpdateSettingsAsync(string clientId, string? clientSecretCifrado, bool enabled, bool pollingEnabled,
        bool importEnabled, bool projectionEnabled, bool commandsEnabled, bool exceptionsEnabled, string? updatedBy,
        bool completionEnabled = false, bool retentionEnabled = false, int retentionDays = 30, bool dineInEnabled = false, CancellationToken ct = default);
    Task<IReadOnlyList<IfoodConnectionRow>> ListConnectionsAsync(CancellationToken ct = default);
    // Conexão mais recente de UMA loja; nunca devolve dados de outro estabelecimento.
    Task<IfoodConnectionRow?> GetStoreConnectionAsync(Guid estabelecimentoId, CancellationToken ct = default);
    Task AuthorizeConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default);
    // A propria loja informa o merchant: a conexao fica 'pending' (nada e consultado nem importado) ate a Gestao autorizar.
    Task RequestStoreConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default);
    // A propria loja desfaz a conexao (pendente ou autorizada). Falso se nao havia conexao ativa.
    Task<bool> DisconnectStoreAsync(Guid estabelecimentoId, CancellationToken ct = default);
    Task RevokeConnectionAsync(Guid merchantId, CancellationToken ct = default);
    // Valor que os pedidos NOVOS da loja herdam para a entrega Zippy; não altera pedidos já importados.
    // applyToOpenOrders também muda os pedidos de entrega da loja que ainda não terminaram no iFood.
    Task<int> SetConnectionDeliveryAsync(Guid merchantId, bool enabled, bool applyToOpenOrders = false, CancellationToken ct = default);
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
       commands_enabled AS CommandsEnabled, exceptions_enabled AS ExceptionsEnabled, completion_enabled AS CompletionEnabled,
       dine_in_enabled AS DineInEnabled, retention_enabled AS RetentionEnabled, retention_days AS RetentionDays,
       updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy
FROM ifood_app_settings WHERE id=1
""", cancellationToken: ct));
        return row ?? new IfoodRuntimeSettings();
    }

    public async Task UpdateSettingsAsync(string clientId, string? clientSecretCifrado, bool enabled, bool pollingEnabled,
        bool importEnabled, bool projectionEnabled, bool commandsEnabled, bool exceptionsEnabled, string? updatedBy,
        bool completionEnabled = false, bool retentionEnabled = false, int retentionDays = 30, bool dineInEnabled = false, CancellationToken ct = default)
    {
        if (retentionDays is < 7 or > 365) throw new IfoodIntegrationException("IFOOD_CONFIG_INVALID_RETENTION");
        await using var connection = await _source.OpenConnectionAsync(ct);
        // clientSecretCifrado nulo preserva o segredo já gravado (toggle isolado não exige redigitar).
        await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_app_settings SET client_id=@ClientId,
    client_secret_cifrado=COALESCE(@ClientSecretCifrado, client_secret_cifrado),
    enabled=@Enabled, polling_enabled=@PollingEnabled, import_enabled=@ImportEnabled,
    projection_enabled=@ProjectionEnabled, commands_enabled=@CommandsEnabled, exceptions_enabled=@ExceptionsEnabled, completion_enabled=@CompletionEnabled,
    dine_in_enabled=@DineInEnabled, retention_enabled=@RetentionEnabled, retention_days=@RetentionDays,
    updated_at_utc=NOW(), updated_by=@UpdatedBy
WHERE id=1
""", new { ClientId = clientId, ClientSecretCifrado = clientSecretCifrado, Enabled = enabled, PollingEnabled = pollingEnabled,
            ImportEnabled = importEnabled, ProjectionEnabled = projectionEnabled, CommandsEnabled = commandsEnabled,
            ExceptionsEnabled = exceptionsEnabled, CompletionEnabled = completionEnabled, DineInEnabled = dineInEnabled, RetentionEnabled = retentionEnabled, RetentionDays = retentionDays, UpdatedBy = updatedBy }, cancellationToken: ct));
    }

    // Uma linha por par (merchant, loja): o historico de lojas antigas fica, so uma conexao fica ativa (revoked_at_utc nulo).
    private const string ConnectionColumns = """
merchant_id AS MerchantId, estabelecimento_id AS EstabelecimentoId, status AS Status,
       authorized_at_utc AS AuthorizedAtUtc, revoked_at_utc AS RevokedAtUtc, last_event_at_utc AS LastEventAtUtc,
       created_at_utc AS CreatedAtUtc, zippy_delivery_enabled AS ZippyDeliveryEnabled,
       poll_state AS PollState, poll_error_code AS PollErrorCode, poll_failures AS PollFailures,
       poll_blocked_until_utc AS PollBlockedUntilUtc, last_poll_at_utc AS LastPollAtUtc, last_poll_ok_at_utc AS LastPollOkAtUtc,
       remote_check_code AS RemoteCheckCode, remote_checked_at_utc AS RemoteCheckedAtUtc
""";

    public async Task<IReadOnlyList<IfoodConnectionRow>> ListConnectionsAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<IfoodConnectionRow>(new CommandDefinition(
            $"SELECT {ConnectionColumns} FROM ifood_connections ORDER BY (revoked_at_utc IS NULL) DESC, updated_at_utc DESC", cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IfoodConnectionRow?> GetStoreConnectionAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (estabelecimentoId == Guid.Empty) return null;
        await using var connection = await _source.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<IfoodConnectionRow>(new CommandDefinition(
            $"SELECT {ConnectionColumns} FROM ifood_connections WHERE estabelecimento_id=@EstabelecimentoId ORDER BY (revoked_at_utc IS NULL) DESC, updated_at_utc DESC LIMIT 1",
            new { EstabelecimentoId = estabelecimentoId }, cancellationToken: ct));
    }

    // Trava todas as linhas do merchant (qualquer loja) e devolve a ativa, se houver. Serializa autorizar/transferir/revogar
    // e tambem o polling, que le a conexao com FOR SHARE antes de gravar eventos.
    internal static async Task<(IfoodConnectionRow? Active, IfoodConnectionRow? Pair)> LockMerchantAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid merchantId, Guid estabelecimentoId, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<IfoodConnectionRow>(new CommandDefinition(
            $"SELECT {ConnectionColumns} FROM ifood_connections WHERE merchant_id=@MerchantId FOR UPDATE",
            new { MerchantId = merchantId }, tx, cancellationToken: ct))).AsList();
        return (rows.FirstOrDefault(r => r.RevokedAtUtc is null), rows.FirstOrDefault(r => r.EstabelecimentoId == estabelecimentoId));
    }

    internal static async Task<bool> StoreHasOtherActiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid merchantId, Guid estabelecimentoId, CancellationToken ct)
        => await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM ifood_connections WHERE estabelecimento_id=@EstabelecimentoId AND merchant_id<>@MerchantId AND revoked_at_utc IS NULL)",
            new { EstabelecimentoId = estabelecimentoId, MerchantId = merchantId }, tx, cancellationToken: ct));

    internal static Task ResetHealthAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid merchantId, Guid estabelecimentoId, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET poll_state=NULL, poll_error_code=NULL, poll_failures=0, poll_blocked_until_utc=NULL,
    last_poll_at_utc=NULL, last_poll_ok_at_utc=NULL, remote_check_code=NULL, remote_checked_at_utc=NULL
WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));

    public async Task AuthorizeConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (merchantId == Guid.Empty || estabelecimentoId == Guid.Empty)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (active, pair) = await LockMerchantAsync(connection, tx, merchantId, estabelecimentoId, ct);
        if (active is not null && active.EstabelecimentoId != estabelecimentoId)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE");
        if (await StoreHasOtherActiveAsync(connection, tx, merchantId, estabelecimentoId, ct))
            throw new IfoodIntegrationException("IFOOD_CONNECTION_STORE_HAS_ACTIVE");
        try
        {
            if (pair is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='authorized', authorized_at_utc=NOW(), revoked_at_utc=NULL, updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
                // Reautorizar uma conexao que estava revogada recomeca a saude: a quarentena antiga nao vale mais.
                if (pair.RevokedAtUtc is not null || pair.Status != "authorized")
                    await ResetHealthAsync(connection, tx, merchantId, estabelecimentoId, ct);
            }
            else
                await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connections(merchant_id, estabelecimento_id, status, authorized_at_utc, revoked_at_utc)
VALUES (@MerchantId, @EstabelecimentoId, 'authorized', NOW(), NULL)
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        }
        // Corrida entre duas lojas pelo mesmo merchant (ainda sem linha para travar): o indice unico do ativo decide.
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE");
        }
        await tx.CommitAsync(ct);
    }

    public async Task RequestStoreConnectionAsync(Guid merchantId, Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (merchantId == Guid.Empty || estabelecimentoId == Guid.Empty)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (active, pair) = await LockMerchantAsync(connection, tx, merchantId, estabelecimentoId, ct);
        if (active is not null && active.EstabelecimentoId != estabelecimentoId)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE");
        if (await StoreHasOtherActiveAsync(connection, tx, merchantId, estabelecimentoId, ct))
            throw new IfoodIntegrationException("IFOOD_CONNECTION_STORE_HAS_ACTIVE");
        // Ja ativa nesta loja (pendente ou autorizada): nada a mudar. So um merchant novo ou revogado fica pendente.
        if (active is not null) { await tx.CommitAsync(ct); return; }
        try
        {
            if (pair is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='pending', authorized_at_utc=NULL, revoked_at_utc=NULL, updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
                await ResetHealthAsync(connection, tx, merchantId, estabelecimentoId, ct);
            }
            else
                await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connections(merchant_id, estabelecimento_id, status, authorized_at_utc, revoked_at_utc)
VALUES (@MerchantId, @EstabelecimentoId, 'pending', NULL, NULL)
""", new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new IfoodIntegrationException("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE");
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DisconnectStoreAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        if (estabelecimentoId == Guid.Empty) return false;
        await using var connection = await _source.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='revoked', revoked_at_utc=NOW(), updated_at_utc=NOW()
WHERE estabelecimento_id=@EstabelecimentoId AND revoked_at_utc IS NULL
""", new { EstabelecimentoId = estabelecimentoId }, cancellationToken: ct));
        return affected > 0;
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

    public async Task<int> SetConnectionDeliveryAsync(Guid merchantId, bool enabled, bool applyToOpenOrders = false, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET zippy_delivery_enabled=@Enabled, updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND status='authorized' AND revoked_at_utc IS NULL
""", new { MerchantId = merchantId, Enabled = enabled }, tx, cancellationToken: ct));
        if (affected == 0) throw new IfoodIntegrationException("IFOOD_CONNECTION_NOT_FOUND");
        var orders = 0;
        if (applyToOpenOrders)
            // Só entrega da própria loja e só pedidos ainda abertos: nada de pedido concluído, cancelado ou entregue pelo iFood.
            orders = await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_order_links l SET zippy_delivery_enabled=@Enabled
FROM ifood_order_snapshots s
WHERE l.merchant_id=@MerchantId AND s.merchant_id=l.merchant_id AND s.external_order_id=l.external_order_id
  AND s.order_type='DELIVERY' AND s.delivered_by='MERCHANT'
  AND COALESCE(s.remote_status,'PLACED') NOT IN ('CANCELLED','CONCLUDED') AND l.zippy_delivery_enabled<>@Enabled
""", new { MerchantId = merchantId, Enabled = enabled }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return orders;
    }
}
