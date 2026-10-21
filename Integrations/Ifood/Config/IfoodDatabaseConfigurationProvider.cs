using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Integrations.Ifood.Config;

// Fonte de configuração executada ANTES do container de DI existir (parte do IConfigurationBuilder
// do boot), por isso usa uma conexão própria em vez do NpgsqlDataSource da aplicação. Nunca decifra
// segredos aqui: só repassa o texto cifrado adiante como Ifood:ClientSecretCifrado; a decifragem
// acontece em tempo de uso, via IfoodSecretProtector, dentro de serviços já resolvidos pelo DI.
public sealed class IfoodDatabaseConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    public IfoodDatabaseConfigurationSource(string connectionString, TimeSpan pollInterval)
    { _connectionString = connectionString; _pollInterval = pollInterval; }
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new IfoodDatabaseConfigurationProvider(_connectionString, _pollInterval);
}

public sealed class IfoodDatabaseConfigurationProvider : ConfigurationProvider, IDisposable
{
    // Limite do IfoodOptionsValidator para a lista de merchants.
    private const int MaxMerchants = 100;
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private Timer? _timer;

    public IfoodDatabaseConfigurationProvider(string connectionString, TimeSpan pollInterval)
    { _connectionString = connectionString; _pollInterval = pollInterval; }

    public override void Load()
    {
        LoadOnce();
        _timer ??= new Timer(_ => LoadOnce(), null, _pollInterval, _pollInterval);
    }

    private void LoadOnce()
    {
        try
        {
            var data = FetchAsync().GetAwaiter().GetResult();
            if (data == null) return; // tabela ainda não migrada: mantém o último snapshot (ou nenhum, no primeiro load)
            Data = data;
            OnReload();
        }
        catch
        {
            // Falha ao consultar a configuração central (banco indisponível, etc.): nunca derruba o
            // boot nem muda o último snapshot válido. Próxima rodada do timer tenta de novo.
        }
    }

    private async Task<Dictionary<string, string?>?> FetchAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var settings = await connection.QuerySingleOrDefaultAsync<SettingsRow>(
            "SELECT client_id, client_secret_cifrado, enabled, polling_enabled, import_enabled, projection_enabled, commands_enabled, exceptions_enabled, completion_enabled, dine_in_enabled, retention_enabled, retention_days FROM ifood_app_settings WHERE id=1");
        if (settings == null) return null;
        // Mantem a credencial do merchant para concluir entregas ja em rota. Polling e comandos
        // novos sao filtrados separadamente, no snapshot e nas filas do banco.
        List<ConnectionRow> connections;
        try
        {
            connections = (await connection.QueryAsync<ConnectionRow>(@"
SELECT c.merchant_id, c.estabelecimento_id, e.situacao_comercial,
       ('IFOOD'::modulo_enum = ANY (COALESCE(e.modulos_ativos, '{}'::modulo_enum[]))) AS ifood_active
  FROM ifood_connections c
  JOIN estabelecimentos e ON e.id = c.estabelecimento_id
 WHERE c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL
 ORDER BY c.merchant_id")).AsList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            // Antes da migracao 20261020_02 a coluna nao existe: mantem o comportamento antigo.
            connections = (await connection.QueryAsync<ConnectionRow>(
                "SELECT merchant_id, estabelecimento_id FROM ifood_connections WHERE status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL ORDER BY merchant_id")).AsList();
        }

        // Nunca emite uma combinação que o IfoodOptionsValidator rejeitaria: sem credenciais, "enabled"
        // nunca sobe; sem nenhuma loja autorizada, polling, comandos, cancelamento e conclusão nunca sobem
        // mesmo que a tela peça. As listas de merchants e de ações vêm das conexões e das regras, não do ambiente.
        var merchants = connections.Take(MaxMerchants).ToList();
        var pollingMerchants = connections
            .Where(c => c.situacao_comercial is not ("bloqueado" or "implantacao") &&
                        (c.situacao_comercial == "teste" || c.ifood_active))
            .Take(MaxMerchants).ToList();
        var credentialsPresent = !string.IsNullOrWhiteSpace(settings.client_id) && !string.IsNullOrWhiteSpace(settings.client_secret_cifrado);
        var enabled = settings.enabled && credentialsPresent;
        var commandsOn = enabled && settings.commands_enabled && merchants.Count > 0;
        var exceptionsOn = enabled && settings.exceptions_enabled && merchants.Count > 0;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ifood:Enabled"] = enabled ? "true" : "false",
            ["Ifood:ClientId"] = settings.client_id,
            ["Ifood:ClientSecretCifrado"] = settings.client_secret_cifrado,
            ["Ifood:PollingEnabled"] = (enabled && settings.polling_enabled && pollingMerchants.Count > 0) ? "true" : "false",
            ["Ifood:ImportEnabled"] = (enabled && settings.import_enabled) ? "true" : "false",
            ["Ifood:ProjectionEnabled"] = (enabled && settings.projection_enabled) ? "true" : "false",
            ["Ifood:CommandsEnabled"] = commandsOn ? "true" : "false",
            ["Ifood:ExceptionsEnabled"] = exceptionsOn ? "true" : "false",
            ["Ifood:CompletionEnabled"] = (commandsOn && settings.completion_enabled) ? "true" : "false",
            ["Ifood:DineInEnabled"] = (commandsOn && settings.dine_in_enabled) ? "true" : "false",
            ["Ifood:RetentionEnabled"] = (enabled && settings.retention_enabled) ? "true" : "false",
            ["Ifood:RetentionDays"] = Math.Clamp((int)settings.retention_days, 7, 365).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < merchants.Count; i++)
        {
            var id = merchants[i].merchant_id.ToString(); var store = merchants[i].estabelecimento_id.ToString();
            if (commandsOn)
            {
                result[$"Ifood:CommandMerchants:{i}:MerchantId"] = id;
                result[$"Ifood:CommandMerchants:{i}:EstabelecimentoId"] = store;
            }
            if (exceptionsOn)
            {
                result[$"Ifood:ExceptionMerchants:{i}:MerchantId"] = id;
                result[$"Ifood:ExceptionMerchants:{i}:EstabelecimentoId"] = store;
            }
        }
        for (var i = 0; i < pollingMerchants.Count; i++)
        {
            result[$"Ifood:PollingMerchants:{i}:MerchantId"] = pollingMerchants[i].merchant_id.ToString();
            result[$"Ifood:PollingMerchants:{i}:EstabelecimentoId"] = pollingMerchants[i].estabelecimento_id.ToString();
        }
        if (commandsOn)
            for (var i = 0; i < Commands.IfoodCommandRules.All.Length; i++) result[$"Ifood:AllowedCommands:{i}"] = Commands.IfoodCommandRules.All[i];
        if (exceptionsOn)
            for (var i = 0; i < Exceptions.IfoodExceptionRules.All.Length; i++) result[$"Ifood:AllowedExceptionActions:{i}"] = Exceptions.IfoodExceptionRules.All[i];
        return result;
    }

    public void Dispose() => _timer?.Dispose();

    private sealed class SettingsRow
    {
        public string client_id { get; set; } = "";
        public string? client_secret_cifrado { get; set; }
        public bool enabled { get; set; }
        public bool polling_enabled { get; set; }
        public bool import_enabled { get; set; }
        public bool projection_enabled { get; set; }
        public bool commands_enabled { get; set; }
        public bool exceptions_enabled { get; set; }
        public bool completion_enabled { get; set; }
        public bool dine_in_enabled { get; set; }
        public bool retention_enabled { get; set; }
        public short retention_days { get; set; } = 30;
    }
    private sealed class ConnectionRow
    {
        public Guid merchant_id { get; set; }
        public Guid estabelecimento_id { get; set; }
        public string? situacao_comercial { get; set; }
        public bool ifood_active { get; set; }
    }
}

public static class IfoodDatabaseConfigurationExtensions
{
    public static IConfigurationBuilder AddIfoodDatabaseConfig(this IConfigurationBuilder builder, string connectionString, TimeSpan? pollInterval = null)
        => builder.Add(new IfoodDatabaseConfigurationSource(connectionString, pollInterval ?? TimeSpan.FromSeconds(15)));
}
