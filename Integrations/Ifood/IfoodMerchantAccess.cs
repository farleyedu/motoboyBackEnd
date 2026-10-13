using Dapper;
using Npgsql;
using Microsoft.Extensions.Options;

namespace APIBack.Integrations.Ifood;

public interface IIfoodConnectionRepository
{
    Task<bool> IsAuthorizedAsync(Guid estabelecimentoId, Guid merchantId, CancellationToken cancellationToken = default);
}

public sealed class IfoodConnectionRepository : IIfoodConnectionRepository
{
    private readonly NpgsqlDataSource _dataSource;
    public IfoodConnectionRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> IsAuthorizedAsync(Guid estabelecimentoId, Guid merchantId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS (
    SELECT 1 FROM ifood_connections
    WHERE estabelecimento_id=@EstabelecimentoId AND merchant_id=@MerchantId
      AND status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL
)
""", new { EstabelecimentoId = estabelecimentoId, MerchantId = merchantId }, cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        { throw new IfoodIntegrationException("IFOOD_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException)
        { throw new IfoodIntegrationException("IFOOD_CONNECTION_UNAVAILABLE", retryable: true); }
    }
}

public sealed class IfoodMerchantAccess
{
    private readonly IIfoodConnectionRepository _connections;
    private readonly IIfoodTokenProvider _tokens;
    private readonly IfoodOptions _options;
    public IfoodMerchantAccess(IIfoodConnectionRepository connections, IIfoodTokenProvider tokens, IOptions<IfoodOptions> options)
        => (_connections, _tokens, _options) = (connections, tokens, options.Value);

    // Porta interna para futuros consumidores. Não concede autorização nem habilita uma loja.
    public async Task<IfoodAccessToken> GetTokenAsync(Guid estabelecimentoId, Guid merchantId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new IfoodIntegrationException("IFOOD_DISABLED");
        if (estabelecimentoId == Guid.Empty || merchantId == Guid.Empty)
            throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
        await EnsureAuthorizedAsync(estabelecimentoId, merchantId, cancellationToken);
        var token = await _tokens.GetAsync(cancellationToken);
        // Uma revogação durante a renovação não pode liberar o token para o consumidor da loja.
        await EnsureAuthorizedAsync(estabelecimentoId, merchantId, cancellationToken);
        return token;
    }

    private async Task EnsureAuthorizedAsync(Guid store, Guid merchant, CancellationToken cancellationToken)
    {
        if (!await _connections.IsAuthorizedAsync(store, merchant, cancellationToken))
            throw new IfoodIntegrationException("IFOOD_MERCHANT_NOT_AUTHORIZED");
    }
}
