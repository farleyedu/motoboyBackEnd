using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string RuntimeSettingsFile = "20261014_02_ifood_runtime_settings.sql";
    private const string RuntimeFlagsFile = "20261014_04_ifood_runtime_flags.sql";
    private const string RuntimeFlagsRollbackFile = "20261014_04_ifood_runtime_flags.down.sql";
    private const string RuntimeSettingsRollbackFile = "20261014_02_ifood_runtime_settings.down.sql";

    [DeliveryDatabaseFact]
    public async Task RuntimeSettingsMigrationSeedsSingleDisabledRowAndIsIdempotent()
    {
        await using var db = await Database.Create();
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_app_settings"));
        var settings = await db.RuntimeSettings.GetSettingsAsync();
        Assert.False(settings.Enabled); Assert.False(settings.PollingEnabled); Assert.Equal("", settings.ClientId);
        Assert.Null(settings.ClientSecretCifrado);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_app_settings"));
    }

    [DeliveryDatabaseFact]
    public async Task UpdateSettingsPreservesSecretWhenNullAndOverwritesWhenProvided()
    {
        await using var db = await Database.Create();
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.first", true, false, false, false, false, false, "user-1");
        var afterFirst = await db.RuntimeSettings.GetSettingsAsync();
        Assert.Equal("v1.key1.first", afterFirst.ClientSecretCifrado); Assert.True(afterFirst.Enabled);

        // Toggle isolado (segredo nulo no request) não apaga o segredo já gravado.
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", null, true, true, false, false, false, false, "user-1");
        var afterToggle = await db.RuntimeSettings.GetSettingsAsync();
        Assert.Equal("v1.key1.first", afterToggle.ClientSecretCifrado); Assert.True(afterToggle.PollingEnabled);

        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.second", true, true, false, false, false, false, "user-1");
        Assert.Equal("v1.key1.second", (await db.RuntimeSettings.GetSettingsAsync()).ClientSecretCifrado);
    }

    [DeliveryDatabaseFact]
    public async Task AuthorizeRejectsMovingAnAlreadyLinkedMerchantToADifferentStoreButReauthorizesSameStore()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        var connections = await db.RuntimeSettings.ListConnectionsAsync();
        Assert.Single(connections, c => c.MerchantId == merchant && c.Status == "authorized");

        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreB));

        await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        Assert.Equal("revoked", (await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == merchant).Status);
        // Revogar de novo não encontra linha com revoked_at_utc ainda nulo.
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.RuntimeSettings.RevokeConnectionAsync(merchant));

        // Reautorizar o mesmo merchant na mesma loja depois de revogado volta a status=authorized.
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        Assert.Equal("authorized", (await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == merchant).Status);
    }

    [DeliveryDatabaseFact]
    public async Task StoreConnectionNeverReturnsAnotherStoresMerchantAndPrefersTheActiveOne()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        var merchantA = Guid.NewGuid(); var merchantB = Guid.NewGuid(); var oldA = Guid.NewGuid();
        Assert.Null(await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA));

        await db.RuntimeSettings.AuthorizeConnectionAsync(oldA, db.StoreA);
        await db.RuntimeSettings.RevokeConnectionAsync(oldA);
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchantA, db.StoreA);
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchantB, db.StoreB);

        var a = await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA);
        Assert.NotNull(a); Assert.Equal(merchantA, a!.MerchantId); Assert.Equal("authorized", a.Status); Assert.Equal(db.StoreA, a.EstabelecimentoId);
        var b = await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreB);
        Assert.Equal(merchantB, b!.MerchantId); Assert.Equal(db.StoreB, b.EstabelecimentoId);
        Assert.Null(await db.RuntimeSettings.GetStoreConnectionAsync(Guid.NewGuid()));
        Assert.Null(await db.RuntimeSettings.GetStoreConnectionAsync(Guid.Empty));
    }

    [DeliveryDatabaseFact]
    public async Task ProviderDerivesCommandAndExceptionAllowlistsFromConnectionsAndNeverEmitsAnInvalidCombination()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        // Tudo ligado na tela, mas sem nenhum merchant autorizado: nada que exija lista de merchants pode subir.
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.secret", true, true, true, true, true, true, "user-1", completionEnabled: true);
        var none = Build(db);
        Assert.Equal("true", none["Ifood:Enabled"]);
        foreach (var key in new[] { "PollingEnabled", "CommandsEnabled", "ExceptionsEnabled", "CompletionEnabled" })
            Assert.Equal("false", none[$"Ifood:{key}"]);
        Assert.True(IsValid(none));

        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        var all = Build(db);
        Assert.Equal("true", all["Ifood:CommandsEnabled"]); Assert.Equal("true", all["Ifood:ExceptionsEnabled"]); Assert.Equal("true", all["Ifood:CompletionEnabled"]);
        Assert.Equal(merchant.ToString(), all["Ifood:CommandMerchants:0:MerchantId"]);
        Assert.Equal(db.StoreA.ToString(), all["Ifood:ExceptionMerchants:0:EstabelecimentoId"]);
        Assert.Equal(4, all.Keys.Count(k => k.StartsWith("Ifood:AllowedCommands:")));
        Assert.Equal(4, all.Keys.Count(k => k.StartsWith("Ifood:AllowedExceptionActions:")));
        Assert.True(IsValid(all));

        // Conclusão nunca sobe sem Comandos, mesmo com o interruptor próprio ligado.
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", null, true, true, true, true, false, true, "user-1", completionEnabled: true);
        var noCommands = Build(db);
        Assert.Equal("false", noCommands["Ifood:CommandsEnabled"]); Assert.Equal("false", noCommands["Ifood:CompletionEnabled"]);
        Assert.DoesNotContain("Ifood:CommandMerchants:0:MerchantId", noCommands.Keys); Assert.DoesNotContain("Ifood:AllowedCommands:0", noCommands.Keys);
        Assert.True(IsValid(noCommands));

        // Revogar o único merchant derruba de novo tudo o que depende de merchant.
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", null, true, true, true, true, true, true, "user-1", completionEnabled: true);
        await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        var revoked = Build(db);
        Assert.Equal("false", revoked["Ifood:PollingEnabled"]); Assert.Equal("false", revoked["Ifood:CommandsEnabled"]);
        Assert.True(IsValid(revoked));
    }

    // Lê o que o provider emite (e a chave de criptografia, que continua só no ambiente) e valida como o app faria.
    private static Dictionary<string, string?> Build(Database db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ifood:ActiveEncryptionKeyId"] = "key1",
                ["Ifood:EncryptionKeys:key1"] = Convert.ToBase64String(new byte[32]),
            })
            .AddIfoodDatabaseConfig(db.Source.ConnectionString, TimeSpan.FromHours(1)).Build();
        return configuration.AsEnumerable().Where(pair => pair.Key.StartsWith("Ifood:")).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
    private static bool IsValid(Dictionary<string, string?> data)
    {
        var options = new ConfigurationBuilder().AddInMemoryCollection(data.ToDictionary(p => p.Key, p => p.Value)).Build()
            .GetSection("Ifood").Get<IfoodOptions>() ?? new IfoodOptions();
        return new IfoodOptionsValidator().Validate(null, options).Succeeded;
    }

    [DeliveryDatabaseFact]
    public async Task ZippyDeliveryFlagIsPerAuthorizedConnectionAndDefaultsToOff()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        var merchant = Guid.NewGuid(); var other = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        await db.RuntimeSettings.AuthorizeConnectionAsync(other, db.StoreB);
        Assert.False((await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.ZippyDeliveryEnabled);

        await db.RuntimeSettings.SetConnectionDeliveryAsync(merchant, true);
        Assert.True((await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.ZippyDeliveryEnabled);
        Assert.False((await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreB))!.ZippyDeliveryEnabled);

        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.RuntimeSettings.SetConnectionDeliveryAsync(Guid.NewGuid(), true));
        await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.RuntimeSettings.SetConnectionDeliveryAsync(merchant, false));
    }

    [DeliveryDatabaseFact]
    public async Task RuntimeFlagsRollbackRefusesWhileAnyFlagIsOnAndIsIdempotentOnMigrate()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.Migrate(RuntimeFlagsFile);
        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        await db.RuntimeSettings.SetConnectionDeliveryAsync(merchant, true);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", RuntimeFlagsRollbackFile))));
        await db.RuntimeSettings.SetConnectionDeliveryAsync(merchant, false);
        await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", RuntimeFlagsRollbackFile)));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.columns WHERE column_name='zippy_delivery_enabled' AND table_name='ifood_connections'"));
    }

    [DeliveryDatabaseFact]
    public async Task DatabaseConfigurationProviderNeverEmitsPollingEnabledWithoutAnAuthorizedMerchant()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.secret", true, true, false, false, false, false, "user-1");
        using var provider = new IfoodDatabaseConfigurationProvider(db.Source.ConnectionString, TimeSpan.FromHours(1));
        provider.Load();
        Assert.Equal("true", Get(provider, "Ifood:Enabled"));
        // Flag pede polling, mas nenhum merchant está autorizado ainda: provider degrada para false.
        Assert.Equal("false", Get(provider, "Ifood:PollingEnabled"));
        Assert.Null(Get(provider, "Ifood:PollingMerchants:0:MerchantId"));

        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        using var reloaded = new IfoodDatabaseConfigurationProvider(db.Source.ConnectionString, TimeSpan.FromHours(1));
        reloaded.Load();
        Assert.Equal("true", Get(reloaded, "Ifood:PollingEnabled"));
        Assert.Equal(merchant.ToString(), Get(reloaded, "Ifood:PollingMerchants:0:MerchantId"));
        Assert.Equal(db.StoreA.ToString(), Get(reloaded, "Ifood:PollingMerchants:0:EstabelecimentoId"));
        Assert.Equal("v1.key1.secret", Get(reloaded, "Ifood:ClientSecretCifrado"));
    }
    private static string? Get(Microsoft.Extensions.Configuration.IConfigurationProvider provider, string key)
        => provider.TryGet(key, out var value) ? value : null;

    [DeliveryDatabaseFact]
    public async Task RollbackRefusesWhileEnabledAndSucceedsAfterTurningOff()
    {
        await using var db = await Database.Create();
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.secret", true, false, false, false, false, false, "user-1");
        var rollbackSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", RuntimeSettingsRollbackFile));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(rollbackSql));
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", null, false, false, false, false, false, false, "user-1");
        await db.Execute(rollbackSql);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='ifood_app_settings'"));
    }
}
