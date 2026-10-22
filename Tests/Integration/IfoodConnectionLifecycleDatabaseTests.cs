using APIBack.Integrations.Ifood;
using Xunit;

namespace APIBack.Tests.Integration;

// Ciclo de vida das conexoes iFood contra PostgreSQL real: nada aqui pede SQL manual nem apaga historico.
public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private static async Task<Database> LifecycleDb()
    {
        var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(RuntimeSettingsFile);
        await db.Migrate(RuntimeFlagsFile);
        await db.Migrate(ReconciliationFile);
        await db.Migrate(LifecycleFile);
        return db;
    }
    private static async Task<string> ErrorCode(Func<Task> action) => (await Assert.ThrowsAsync<IfoodIntegrationException>(action)).Code;

    [DeliveryDatabaseFact]
    public async Task LifecycleMigrationIsIdempotentAndKeepsExistingConnections()
    {
        await using var db = await Database.Create();                 // schema antigo com 2 conexoes (merchant unico)
        await db.Migrate(RuntimeSettingsFile); await db.Migrate(RuntimeFlagsFile); await db.Migrate(ReconciliationFile);
        await db.Migrate(LifecycleFile);
        await db.Migrate(LifecycleFile);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM pg_constraint WHERE conrelid='ifood_connections'::regclass AND contype='p' AND array_length(conkey,1)=2"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_tracking_schema_versions WHERE version='20261027_01_ifood_connection_lifecycle'"));
    }

    [DeliveryDatabaseFact]
    public async Task MerchantCanMoveBetweenStoresAndOldHistoryStays()
    {
        await using var db = await LifecycleDb();
        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        // Outra loja nao rouba um merchant ativo.
        Assert.Equal("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE", await ErrorCode(() => db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreB)));
        Assert.Equal("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE", await ErrorCode(() => db.RuntimeSettings.RequestStoreConnectionAsync(merchant, db.StoreB)));

        await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreB);       // sem SQL manual
        var rows = await db.RuntimeSettings.ListConnectionsAsync();
        Assert.Equal(2, rows.Count(r => r.MerchantId == merchant));
        Assert.Single(rows, r => r.MerchantId == merchant && r.RevokedAtUtc == null && r.EstabelecimentoId == db.StoreB);
        Assert.Single(rows, r => r.MerchantId == merchant && r.RevokedAtUtc != null && r.EstabelecimentoId == db.StoreA);
        // A loja antiga ve a propria conexao encerrada, nunca a da loja nova.
        Assert.Equal(db.StoreA, (await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.EstabelecimentoId);
        Assert.NotNull((await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.RevokedAtUtc);
        // E o merchant pode voltar para a loja de antes.
        await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections WHERE merchant_id=@M", new { M = merchant }));
    }

    [DeliveryDatabaseFact]
    public async Task StoreCannotHoldTwoActiveConnections()
    {
        await using var db = await LifecycleDb();
        await db.RuntimeSettings.AuthorizeConnectionAsync(Guid.NewGuid(), db.StoreA);
        Assert.Equal("IFOOD_CONNECTION_STORE_HAS_ACTIVE", await ErrorCode(() => db.RuntimeSettings.AuthorizeConnectionAsync(Guid.NewGuid(), db.StoreA)));
        Assert.Equal("IFOOD_CONNECTION_STORE_HAS_ACTIVE", await ErrorCode(() => db.RuntimeSettings.RequestStoreConnectionAsync(Guid.NewGuid(), db.StoreA)));
        Assert.True(await db.RuntimeSettings.DisconnectStoreAsync(db.StoreA));
        await db.RuntimeSettings.RequestStoreConnectionAsync(Guid.NewGuid(), db.StoreA);   // corrigir o ID: desconectar e informar o certo
        Assert.Equal("pending", (await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.Status);
    }

    [DeliveryDatabaseFact]
    public async Task TwoStoresRacingForTheSameMerchantYieldExactlyOneActiveConnection()
    {
        await using var db = await LifecycleDb();
        for (var round = 0; round < 5; round++)
        {
            var merchant = Guid.NewGuid();
            var results = await Task.WhenAll(
                Try(() => db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA)),
                Try(() => db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreB)));
            Assert.Single(results, r => r == null);
            Assert.Contains("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE", results);
            Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections WHERE merchant_id=@M AND revoked_at_utc IS NULL", new { M = merchant }));
            await db.RuntimeSettings.RevokeConnectionAsync(merchant);
        }
        static async Task<string?> Try(Func<Task> action)
        { try { await action(); return null; } catch (IfoodIntegrationException ex) { return ex.Code; } }
    }

    [DeliveryDatabaseFact]
    public async Task TransferMovesTheMerchantKeepingHistoryAndRefusesWhileWorkIsPending()
    {
        await using var db = await LifecycleDb();
        var merchant = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        var evt = Guid.NewGuid();
        await db.Execute("INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,event_code,payload,state)VALUES(@M,@S,@E,'PLC','{}','queued')", new { M = merchant, S = db.StoreA, E = evt });

        Assert.Equal("IFOOD_CONNECTION_HAS_PENDING_WORK", await ErrorCode(() => db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin")));
        Assert.Equal("authorized", (await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.Status);     // nada mudou

        await db.Execute("UPDATE ifood_events_inbox SET state='processed',processed_at_utc=NOW(),acknowledged_at_utc=NOW()");
        await db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin");

        Assert.NotNull((await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreA))!.RevokedAtUtc);
        var target = (await db.RuntimeSettings.GetStoreConnectionAsync(db.StoreB))!;
        Assert.Equal(merchant, target.MerchantId); Assert.Equal("pending", target.Status);                   // a Gestao ainda precisa autorizar
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE estabelecimento_id=@S", new { S = db.StoreA }));
        Assert.Equal("transferred", (await db.Lifecycle.ListAuditAsync(merchant, null, 10)).First().Action);
        Assert.Equal("IFOOD_CONNECTION_INVALID_INPUT", await ErrorCode(() => db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin")));   // ja esta nessa loja
    }

    [DeliveryDatabaseFact]
    public async Task TransferRefusesOpenOrdersAndUnknownConnections()
    {
        await using var db = await LifecycleDb();
        var merchant = Guid.NewGuid();
        Assert.Equal("IFOOD_CONNECTION_NOT_FOUND", await ErrorCode(() => db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin")));
        await db.RuntimeSettings.AuthorizeConnectionAsync(merchant, db.StoreA);
        await db.Execute("INSERT INTO ifood_order_links(merchant_id,external_order_id,estabelecimento_id,pedido_id)VALUES(@M,@O,@S,1)", new { M = merchant, O = Guid.NewGuid(), S = db.StoreA });
        var order = await db.Scalar<Guid>("SELECT external_order_id FROM ifood_order_links LIMIT 1");
        await db.Execute("INSERT INTO ifood_order_snapshots(merchant_id,external_order_id,estabelecimento_id,order_type,payload)VALUES(@M,@O,@S,'DELIVERY','{}')", new { M = merchant, O = order, S = db.StoreA });
        Assert.Equal("IFOOD_CONNECTION_HAS_PENDING_WORK", await ErrorCode(() => db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin")));
        await db.Execute("UPDATE ifood_order_snapshots SET remote_status='CONCLUDED'");
        await db.Lifecycle.TransferAsync(merchant, db.StoreB, "admin");
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links WHERE estabelecimento_id=@S", new { S = db.StoreA }));   // pedido continua na loja de origem
    }

    [DeliveryDatabaseFact]
    public async Task RemoveOnlyDeletesConnectionsThatNeverHadHistory()
    {
        await using var db = await LifecycleDb();
        var unused = Guid.NewGuid(); var used = Guid.NewGuid();
        await db.RuntimeSettings.RequestStoreConnectionAsync(unused, db.StoreA);
        await db.Lifecycle.RemoveUnusedAsync(unused, db.StoreA, "admin");                         // pendente e sem uso: sai
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections WHERE merchant_id=@M", new { M = unused }));
        Assert.Equal("removed", (await db.Lifecycle.ListAuditAsync(unused, null, 5)).Single().Action);

        await db.RuntimeSettings.AuthorizeConnectionAsync(used, db.StoreA);
        Assert.Equal("IFOOD_CONNECTION_IN_USE", await ErrorCode(() => db.Lifecycle.RemoveUnusedAsync(used, db.StoreA, "admin")));
        await db.Execute("INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,event_code,payload,state)VALUES(@M,@S,@E,'PLC','{}','processed')", new { M = used, S = db.StoreA, E = Guid.NewGuid() });
        await db.RuntimeSettings.RevokeConnectionAsync(used);
        Assert.Equal("IFOOD_CONNECTION_HAS_HISTORY", await ErrorCode(() => db.Lifecycle.RemoveUnusedAsync(used, db.StoreA, "admin")));     // o banco recusa: sem FK manual
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections WHERE merchant_id=@M", new { M = used }));
    }

    [DeliveryDatabaseFact]
    public async Task PollHealthIsPerMerchantBacksOffAndResetsOnRecoveryOrReconnect()
    {
        await using var db = await LifecycleDb();
        var bad = Guid.NewGuid(); var good = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(bad, db.StoreA);
        await db.RuntimeSettings.AuthorizeConnectionAsync(good, db.StoreB);

        await db.Health.RecordPollFailureAsync(bad, "IFOOD_EVENTS_FORBIDDEN");
        var first = await db.Scalar<double>("SELECT EXTRACT(EPOCH FROM poll_blocked_until_utc-NOW()) FROM ifood_connections WHERE merchant_id=@M", new { M = bad });
        await db.Health.RecordPollFailureAsync(bad, "IFOOD_EVENTS_FORBIDDEN");
        var second = await db.Scalar<double>("SELECT EXTRACT(EPOCH FROM poll_blocked_until_utc-NOW()) FROM ifood_connections WHERE merchant_id=@M", new { M = bad });
        Assert.InRange(first, 50, 61); Assert.InRange(second, 110, 121);                             // 60s e depois 120s

        var blocked = await db.Health.BlockedAsync(new[] { bad, good });
        Assert.Contains(bad, blocked.Keys); Assert.DoesNotContain(good, blocked.Keys);               // o outro merchant nunca e afetado
        var badRow = (await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == bad);
        Assert.Equal("forbidden", badRow.PollState); Assert.Equal("IFOOD_EVENTS_FORBIDDEN", badRow.PollErrorCode);
        Assert.Null((await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == good).PollState);

        Assert.True(await db.Health.RetryNowAsync(bad));                                              // "Conferir agora"
        Assert.True((await db.Health.BlockedAsync(new[] { bad }))[bad] <= DateTimeOffset.UtcNow.AddSeconds(1));

        await db.Health.RecordPollSuccessAsync(new[] { bad });
        Assert.Empty(await db.Health.BlockedAsync(new[] { bad }));
        badRow = (await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == bad);
        Assert.Equal("ok", badRow.PollState); Assert.NotNull(badRow.LastPollOkAtUtc); Assert.Equal(0, badRow.PollFailures);

        await db.Health.RecordPollFailureAsync(bad, "IFOOD_EVENTS_FORBIDDEN");
        await db.RuntimeSettings.RevokeConnectionAsync(bad);
        await db.RuntimeSettings.AuthorizeConnectionAsync(bad, db.StoreA);                           // reconectar recomeca limpo
        badRow = (await db.RuntimeSettings.ListConnectionsAsync()).Single(c => c.MerchantId == bad && c.RevokedAtUtc == null);
        Assert.Null(badRow.PollState); Assert.Null(badRow.PollBlockedUntilUtc);
    }

    [DeliveryDatabaseFact]
    public async Task ConfigProviderPollsOnlyTheActiveConnectionNeverRevokedHistory()
    {
        await using var db = await LifecycleDb();
        await db.RuntimeSettings.UpdateSettingsAsync("client-1", "v1.key1.secret", true, true, true, true, false, false, "user-1");
        var old = Guid.NewGuid(); var current = Guid.NewGuid();
        await db.RuntimeSettings.AuthorizeConnectionAsync(old, db.StoreA);
        await db.RuntimeSettings.RevokeConnectionAsync(old);
        await db.RuntimeSettings.AuthorizeConnectionAsync(current, db.StoreA);
        await db.RuntimeSettings.AuthorizeConnectionAsync(old, db.StoreB);                           // o antigo agora vale na outra loja

        using var provider = new APIBack.Integrations.Ifood.Config.IfoodDatabaseConfigurationProvider(db.Source.ConnectionString, TimeSpan.FromHours(1));
        provider.Load();
        provider.TryGet("Ifood:PollingMerchants:0:MerchantId", out var first);
        provider.TryGet("Ifood:PollingMerchants:1:MerchantId", out var second);
        provider.TryGet("Ifood:PollingMerchants:2:MerchantId", out var third);
        Assert.Equal(new[] { old.ToString(), current.ToString() }.OrderBy(x => x), new[] { first, second }.OrderBy(x => x));
        Assert.Null(third);
    }
}
