using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string RuntimeSettingsFile = "20261014_02_ifood_runtime_settings.sql";
    private const string RuntimeSettingsRollbackFile = "20261014_02_ifood_runtime_settings.down.sql";

    [DeliveryDatabaseFact]
    public async Task RuntimeSettingsMigrationSeedsSingleDisabledRowAndIsIdempotent()
    {
        await using var db = await Database.Create();
        await db.Migrate(RuntimeSettingsFile);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_app_settings"));
        var settings = await db.RuntimeSettings.GetSettingsAsync();
        Assert.False(settings.Enabled); Assert.False(settings.PollingEnabled); Assert.Equal("", settings.ClientId);
        Assert.Null(settings.ClientSecretCifrado);
        await db.Migrate(RuntimeSettingsFile);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_app_settings"));
    }

    [DeliveryDatabaseFact]
    public async Task UpdateSettingsPreservesSecretWhenNullAndOverwritesWhenProvided()
    {
        await using var db = await Database.Create();
        await db.Migrate(RuntimeSettingsFile);
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
    public async Task DatabaseConfigurationProviderNeverEmitsPollingEnabledWithoutAnAuthorizedMerchant()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
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
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.secret", true, false, false, false, false, false, "user-1");
        var rollbackSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", RuntimeSettingsRollbackFile));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(rollbackSql));
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", null, false, false, false, false, false, false, "user-1");
        await db.Execute(rollbackSql);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='ifood_app_settings'"));
    }
}
