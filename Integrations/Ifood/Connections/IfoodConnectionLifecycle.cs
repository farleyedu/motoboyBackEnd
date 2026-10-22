using APIBack.Integrations.Ifood.Config;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.Ifood.Connections;

public sealed class IfoodConnectionActivity
{
    public Guid MerchantId { get; set; }
    public Guid EstabelecimentoId { get; set; }
    public int EventsQueued { get; set; }
    public int EventsFailed { get; set; }
    public int EventsReview { get; set; }
    public int EventsUnacknowledged { get; set; }
    public int OpenOrders { get; set; }
    public DateTimeOffset? LastEventReceivedAtUtc { get; set; }
}

public sealed class IfoodAuditRow
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid? EstabelecimentoId { get; set; }
    public string Action { get; set; } = "";
    public string? Actor { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Operacoes de ciclo de vida que nunca apagam historico nem deixam pedido ou evento para tras.</summary>
public interface IIfoodConnectionLifecycle
{
    /// <summary>Revoga a conexao ativa do merchant e abre uma pendente na loja de destino (a Gestao autoriza em seguida).</summary>
    Task TransferAsync(Guid merchantId, Guid toEstabelecimentoId, string? actor, CancellationToken ct = default);
    /// <summary>Apaga uma conexao desfeita/pendente que nunca recebeu evento nem pedido. Com historico, recusa (IFOOD_CONNECTION_HAS_HISTORY).</summary>
    Task RemoveUnusedAsync(Guid merchantId, Guid estabelecimentoId, string? actor, CancellationToken ct = default);
    Task<IReadOnlyList<IfoodConnectionActivity>> ActivityAsync(Guid? estabelecimentoId, CancellationToken ct = default);
    Task AuditAsync(Guid merchantId, Guid? estabelecimentoId, string action, string? actor, string? detail = null, CancellationToken ct = default);
    Task<IReadOnlyList<IfoodAuditRow>> ListAuditAsync(Guid? merchantId, Guid? estabelecimentoId, int limit, CancellationToken ct = default);
    /// <summary>CNPJ cadastrado do estabelecimento (so digitos), ou nulo se a loja ainda nao informou.</summary>
    Task<string?> GetStoreCnpjAsync(Guid estabelecimentoId, CancellationToken ct = default);
}

public sealed class IfoodConnectionLifecycle : IIfoodConnectionLifecycle
{
    private readonly NpgsqlDataSource _source;
    public IfoodConnectionLifecycle(NpgsqlDataSource source) => _source = source;

    public async Task TransferAsync(Guid merchantId, Guid toEstabelecimentoId, string? actor, CancellationToken ct = default)
    {
        if (merchantId == Guid.Empty || toEstabelecimentoId == Guid.Empty)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (active, target) = await IfoodRuntimeSettingsRepository.LockMerchantAsync(connection, tx, merchantId, toEstabelecimentoId, ct);
        if (active is null) throw new IfoodIntegrationException("IFOOD_CONNECTION_NOT_FOUND");
        if (active.EstabelecimentoId == toEstabelecimentoId) throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT");
        if (await IfoodRuntimeSettingsRepository.StoreHasOtherActiveAsync(connection, tx, merchantId, toEstabelecimentoId, ct))
            throw new IfoodIntegrationException("IFOOD_CONNECTION_STORE_HAS_ACTIVE");

        // Pedido aberto ou evento ainda nao tratado/confirmado pertence a loja de origem: mover agora o perderia ou o
        // entregaria a loja errada. A tela mostra o que falta; depois de concluir, a transferencia segue.
        var pending = await connection.QuerySingleAsync<(int Events, int Orders)>(new CommandDefinition("""
SELECT (SELECT COUNT(*) FROM ifood_events_inbox e WHERE e.merchant_id=@MerchantId AND e.estabelecimento_id=@From
         AND (e.state IN ('queued','processing','review') OR e.acknowledged_at_utc IS NULL))::int AS Events,
       (SELECT COUNT(*) FROM ifood_order_snapshots s WHERE s.merchant_id=@MerchantId AND s.estabelecimento_id=@From
         AND COALESCE(s.remote_status,'PLACED') NOT IN ('CANCELLED','CONCLUDED'))::int AS Orders
""", new { MerchantId = merchantId, From = active.EstabelecimentoId }, tx, cancellationToken: ct));
        if (pending.Events > 0 || pending.Orders > 0)
            throw new IfoodIntegrationException("IFOOD_CONNECTION_HAS_PENDING_WORK");

        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='revoked', revoked_at_utc=NOW(), updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND estabelecimento_id=@From AND revoked_at_utc IS NULL
""", new { MerchantId = merchantId, From = active.EstabelecimentoId }, tx, cancellationToken: ct));
            if (target is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_connections SET status='pending', authorized_at_utc=NULL, revoked_at_utc=NULL, updated_at_utc=NOW()
WHERE merchant_id=@MerchantId AND estabelecimento_id=@To
""", new { MerchantId = merchantId, To = toEstabelecimentoId }, tx, cancellationToken: ct));
                await IfoodRuntimeSettingsRepository.ResetHealthAsync(connection, tx, merchantId, toEstabelecimentoId, ct);
            }
            else
                await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connections(merchant_id, estabelecimento_id, status, authorized_at_utc, revoked_at_utc)
VALUES (@MerchantId, @To, 'pending', NULL, NULL)
""", new { MerchantId = merchantId, To = toEstabelecimentoId }, tx, cancellationToken: ct));
            await WriteAuditAsync(connection, tx, merchantId, toEstabelecimentoId, "transferred", actor, $"de {active.EstabelecimentoId:D}", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        { throw new IfoodIntegrationException("IFOOD_CONNECTION_INVALID_INPUT"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new IfoodIntegrationException("IFOOD_CONNECTION_STORE_HAS_ACTIVE"); }
        await tx.CommitAsync(ct);
    }

    public async Task RemoveUnusedAsync(Guid merchantId, Guid estabelecimentoId, string? actor, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var (_, pair) = await IfoodRuntimeSettingsRepository.LockMerchantAsync(connection, tx, merchantId, estabelecimentoId, ct);
        if (pair is null) throw new IfoodIntegrationException("IFOOD_CONNECTION_NOT_FOUND");
        // Conexao em uso (autorizada e ativa) primeiro se desfaz; so entao pode ser removida.
        if (pair.RevokedAtUtc is null && pair.Status == "authorized") throw new IfoodIntegrationException("IFOOD_CONNECTION_IN_USE");
        try
        {
            // As FKs de eventos, pedidos, comandos e vinculos decidem: se algo aponta para a conexao, o banco recusa.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM ifood_connections WHERE merchant_id=@MerchantId AND estabelecimento_id=@EstabelecimentoId",
                new { MerchantId = merchantId, EstabelecimentoId = estabelecimentoId }, tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        { throw new IfoodIntegrationException("IFOOD_CONNECTION_HAS_HISTORY"); }
        await WriteAuditAsync(connection, tx, merchantId, estabelecimentoId, "removed", actor, null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<IfoodConnectionActivity>> ActivityAsync(Guid? estabelecimentoId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<IfoodConnectionActivity>(new CommandDefinition("""
SELECT c.merchant_id AS MerchantId, c.estabelecimento_id AS EstabelecimentoId,
  (SELECT COUNT(*) FROM ifood_events_inbox e WHERE e.merchant_id=c.merchant_id AND e.estabelecimento_id=c.estabelecimento_id AND e.state IN ('queued','processing'))::int AS EventsQueued,
  (SELECT COUNT(*) FROM ifood_events_inbox e WHERE e.merchant_id=c.merchant_id AND e.estabelecimento_id=c.estabelecimento_id AND e.state='failed')::int AS EventsFailed,
  (SELECT COUNT(*) FROM ifood_events_inbox e WHERE e.merchant_id=c.merchant_id AND e.estabelecimento_id=c.estabelecimento_id AND e.state='review')::int AS EventsReview,
  (SELECT COUNT(*) FROM ifood_events_inbox e WHERE e.merchant_id=c.merchant_id AND e.estabelecimento_id=c.estabelecimento_id AND e.acknowledged_at_utc IS NULL)::int AS EventsUnacknowledged,
  (SELECT COUNT(*) FROM ifood_order_snapshots s WHERE s.merchant_id=c.merchant_id AND s.estabelecimento_id=c.estabelecimento_id
     AND COALESCE(s.remote_status,'PLACED') NOT IN ('CANCELLED','CONCLUDED'))::int AS OpenOrders,
  (SELECT MAX(e.received_at_utc) FROM ifood_events_inbox e WHERE e.merchant_id=c.merchant_id AND e.estabelecimento_id=c.estabelecimento_id) AS LastEventReceivedAtUtc
FROM ifood_connections c
WHERE c.revoked_at_utc IS NULL AND (CAST(@Store AS uuid) IS NULL OR c.estabelecimento_id=@Store)
""", new { Store = estabelecimentoId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task AuditAsync(Guid merchantId, Guid? estabelecimentoId, string action, string? actor, string? detail = null, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connection_audit(merchant_id, estabelecimento_id, action, actor, detail) VALUES (@MerchantId, @Store, @Action, @Actor, @Detail)
""", new { MerchantId = merchantId, Store = estabelecimentoId, Action = action, Actor = actor, Detail = detail }, cancellationToken: ct));
    }

    private static Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid merchantId, Guid? store, string action, string? actor, string? detail, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition("""
INSERT INTO ifood_connection_audit(merchant_id, estabelecimento_id, action, actor, detail) VALUES (@MerchantId, @Store, @Action, @Actor, @Detail)
""", new { MerchantId = merchantId, Store = store, Action = action, Actor = actor, Detail = detail }, tx, cancellationToken: ct));

    public async Task<IReadOnlyList<IfoodAuditRow>> ListAuditAsync(Guid? merchantId, Guid? estabelecimentoId, int limit, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<IfoodAuditRow>(new CommandDefinition("""
SELECT id AS Id, merchant_id AS MerchantId, estabelecimento_id AS EstabelecimentoId, action AS Action, actor AS Actor, detail AS Detail, created_at_utc AS CreatedAtUtc
FROM ifood_connection_audit
WHERE (CAST(@Merchant AS uuid) IS NULL OR merchant_id=@Merchant) AND (CAST(@Store AS uuid) IS NULL OR estabelecimento_id=@Store)
ORDER BY created_at_utc DESC, id DESC LIMIT @Limit
""", new { Merchant = merchantId, Store = estabelecimentoId, Limit = Math.Clamp(limit, 1, 200) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<string?> GetStoreCnpjAsync(Guid estabelecimentoId, CancellationToken ct = default)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        var cnpj = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT cnpj_loja FROM estabelecimentos WHERE id=@Id", new { Id = estabelecimentoId }, cancellationToken: ct));
        var digits = IfoodStoreIdentifier.Digits(cnpj);
        return digits.Length == 14 ? digits : null;
    }
}
