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
            "SELECT client_id, client_secret_cifrado, enabled, polling_enabled, import_enabled, projection_enabled, commands_enabled, exceptions_enabled, completion_enabled FROM ifood_app_settings WHERE id=1");
        if (settings == null) return null;
        var connections = (await connection.QueryAsync<ConnectionRow>(
            "SELECT merchant_id, estabelecimento_id FROM ifood_connections WHERE status='authorized' AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL ORDER BY merchant_id")).AsList();

        // Nunca emite uma combinação que o IfoodOptionsValidator rejeitaria: sem credenciais, "enabled"
        // nunca sobe; sem nenhuma loja autorizada, polling, comandos, cancelamento e conclusão nunca sobem
        // mesmo que a tela peça. As listas de merchants e de ações vêm das conexões e das regras, não do ambiente.
        var merchants = connections.Take(MaxMerchants).ToList();
        var credentialsPresent = !string.IsNullOrWhiteSpace(settings.client_id) && !string.IsNullOrWhiteSpace(settings.client_secret_cifrado);
        var enabled = settings.enabled && credentialsPresent;
        var commandsOn = enabled && settings.commands_enabled && merchants.Count > 0;
        var exceptionsOn = enabled && settings.exceptions_enabled && merchants.Count > 0;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ifood:Enabled"] = enabled ? "true" : "false",
            ["Ifood:ClientId"] = settings.client_id,
            ["Ifood:ClientSecretCifrado"] = settings.client_secret_cifrado,
            ["Ifood:PollingEnabled"] = (enabled && settings.polling_enabled && merchants.Count > 0) ? "true" : "false",
            ["Ifood:ImportEnabled"] = (enabled && settings.import_enabled) ? "true" : "false",
            ["Ifood:ProjectionEnabled"] = (enabled && settings.projection_enabled) ? "true" : "false",
            ["Ifood:CommandsEnabled"] = commandsOn ? "true" : "false",
            ["Ifood:ExceptionsEnabled"] = exceptionsOn ? "true" : "false",
            ["Ifood:CompletionEnabled"] = (commandsOn && settings.completion_enabled) ? "true" : "false",
        };
        for (var i = 0; i < merchants.Count; i++)
        {
            var id = merchants[i].merchant_id.ToString(); var store = merchants[i].estabelecimento_id.ToString();
            result[$"Ifood:PollingMerchants:{i}:MerchantId"] = id;
            result[$"Ifood:PollingMerchants:{i}:EstabelecimentoId"] = store;
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
    }
    private sealed class ConnectionRow
    {
        public Guid merchant_id { get; set; }
        public Guid estabelecimento_id { get; set; }
    }
}

public static class IfoodDatabaseConfigurationExtensions
{
    public static IConfigurationBuilder AddIfoodDatabaseConfig(this IConfigurationBuilder builder, string connectionString, TimeSpan? pollInterval = null)
        => builder.Add(new IfoodDatabaseConfigurationSource(connectionString, pollInterval ?? TimeSpan.FromSeconds(15)));
}
