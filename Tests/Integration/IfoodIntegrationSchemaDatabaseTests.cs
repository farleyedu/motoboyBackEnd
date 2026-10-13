using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed class IfoodIntegrationSchemaDatabaseTests
{
    private const string SchemaFile = "20261013_01_ifood_integration_schema.sql";
    private const string VerifyFile = "20261013_02_ifood_integration_verify.sql";

    [DeliveryDatabaseFact]
    public async Task SchemaCanBeAppliedTwiceAndVerificationRequiresItsDependency()
    {
        await using var db = await Database.Create(apply: false);
        await Assert.ThrowsAsync<PostgresException>(() => db.Migrate(VerifyFile));
        await db.Migrate(SchemaFile);
        await db.Migrate(VerifyFile);
        await db.Migrate(SchemaFile);
        await db.Migrate(VerifyFile);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_tracking_schema_versions"));
        Assert.Equal(6, await db.Scalar<int>("SELECT COUNT(*)::int FROM information_schema.tables WHERE table_schema=current_schema() AND table_name LIKE 'ifood_%'"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido"));
    }

    [DeliveryDatabaseFact]
    public async Task MerchantAndOrderLinksCannotCrossStoresButDisplayNumbersMayRepeat()
    {
        await using var db = await Database.Create();
        await db.Link(db.MerchantA, db.StoreA, 1, db.OrderA);
        await db.Link(db.MerchantB, db.StoreB, 2, db.OrderB);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_order_links WHERE display_number='001234'"));
        // Pedido livre de outra loja: não deixar uma violação de unicidade mascarar o teste da FK composta.
        await db.Execute("INSERT INTO pedido VALUES(3,@Store,'preservado')", new { Store = db.StoreB });
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Link(db.MerchantA, db.StoreA, 3, Guid.NewGuid()));
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Link(db.MerchantA, db.StoreB, 3, Guid.NewGuid()));
        await Reject(PostgresErrorCodes.UniqueViolation, () => db.Link(db.MerchantA, db.StoreA, 1, db.OrderA));
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Execute("UPDATE pedido SET id_estabelecimento=@Store WHERE id=1", new { Store = db.StoreB }));
        await Reject(PostgresErrorCodes.UniqueViolation, () => db.Execute("INSERT INTO ifood_connections(merchant_id,estabelecimento_id)VALUES(@Merchant,@Store)", new { Merchant = db.MerchantA, Store = db.StoreB }));
    }

    [DeliveryDatabaseFact]
    public async Task VerificationRejectsMissingTenantConstraintInsteadOfRecordingReady()
    {
        await using var db = await Database.Create();
        await db.Execute("""
DO $$ DECLARE constraint_name TEXT;
BEGIN
    SELECT conname INTO constraint_name FROM pg_constraint
    WHERE conrelid='ifood_order_links'::regclass AND confrelid='pedido'::regclass AND contype='f';
    EXECUTE format('ALTER TABLE ifood_order_links DROP CONSTRAINT %I', constraint_name);
END $$;
""");
        await Assert.ThrowsAsync<PostgresException>(() => db.Migrate(VerifyFile));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_tracking_schema_versions WHERE version='20261013_02_ifood_integration_verify'"));
    }

    [DeliveryDatabaseFact]
    public async Task ConcurrentEventRetriesCreateOnlyOneInboxRecord()
    {
        await using var db = await Database.Create();
        var eventId = Guid.NewGuid();
        const string sql = "INSERT INTO ifood_events_inbox(merchant_id,estabelecimento_id,event_id,event_code,payload)VALUES(@Merchant,@Store,@Event,'PLACED','{}') ON CONFLICT(merchant_id,event_id)DO NOTHING";
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.Execute(sql, new { Merchant = db.MerchantA, Store = db.StoreA, Event = eventId })));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        // O mesmo eventId em outro merchant tem namespace independente.
        await db.Execute(sql, new { Merchant = db.MerchantB, Store = db.StoreB, Event = eventId });
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Execute(sql, new { Merchant = db.MerchantA, Store = db.StoreB, Event = Guid.NewGuid() }));
    }

    [DeliveryDatabaseFact]
    public async Task SnapshotCommandsAndDisputesKeepTheOrderStoreBoundary()
    {
        await using var db = await Database.Create();
        await db.Link(db.MerchantA, db.StoreA, 1, db.OrderA);
        await db.Link(db.MerchantB, db.StoreB, 2, db.OrderB);
        var operation = Guid.NewGuid();
        await db.Command(operation, db.MerchantA, db.StoreA, db.OrderA);
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Command(Guid.NewGuid(), db.MerchantA, db.StoreB, db.OrderA));
        await Reject(PostgresErrorCodes.UniqueViolation, () => db.Command(operation, db.MerchantA, db.StoreA, db.OrderA));
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Command(Guid.NewGuid(), db.MerchantB, db.StoreB, db.OrderB, operation));
        const string snapshot = "INSERT INTO ifood_order_snapshots(merchant_id,external_order_id,estabelecimento_id,order_type,payload)VALUES(@Merchant,@Order,@Store,'FUTURE_TYPE','{}')";
        await db.Execute(snapshot, new { Merchant = db.MerchantA, Order = db.OrderA, Store = db.StoreA });
        Assert.Equal("FUTURE_TYPE", await db.Scalar<string>("SELECT order_type FROM ifood_order_snapshots"));
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Execute(snapshot, new { Merchant = db.MerchantB, Order = db.OrderB, Store = db.StoreA }));
        const string dispute = "INSERT INTO ifood_disputes(merchant_id,dispute_id,external_order_id,estabelecimento_id,expires_at_utc,timeout_action,payload,response_operation_id)VALUES(@Merchant,@Dispute,@Order,@Store,NOW(),'FUTURE_ACTION','{}',@Response)";
        await Reject(PostgresErrorCodes.ForeignKeyViolation, () => db.Execute(dispute, new { Merchant = db.MerchantB, Dispute = Guid.NewGuid(), Order = db.OrderB, Store = db.StoreB, Response = operation }));
        await db.Execute(dispute, new { Merchant = db.MerchantA, Dispute = Guid.NewGuid(), Order = db.OrderA, Store = db.StoreA, Response = operation });
    }

    [DeliveryDatabaseFact]
    public async Task ProcessingRequiresACompleteLeaseAndNegativeAmountsAreRejected()
    {
        await using var db = await Database.Create();
        await db.Link(db.MerchantA, db.StoreA, 1, db.OrderA);
        var operation = Guid.NewGuid();
        await db.Command(operation, db.MerchantA, db.StoreA, db.OrderA);
        await Reject(PostgresErrorCodes.CheckViolation, () => db.Execute("UPDATE ifood_outbound_commands SET state='processing'"));
        await db.Execute("UPDATE ifood_outbound_commands SET state='processing',lease_id=@Lease,lease_until_utc=NOW()+INTERVAL '1 minute'", new { Lease = Guid.NewGuid() });
        await Reject(PostgresErrorCodes.CheckViolation, () => db.Execute("UPDATE ifood_outbound_commands SET state='succeeded'"));
        await db.Execute("UPDATE ifood_outbound_commands SET state='uncertain',lease_id=NULL,lease_until_utc=NULL");
        Assert.Equal("uncertain", await db.Scalar<string>("SELECT state FROM ifood_outbound_commands"));
        await Reject(PostgresErrorCodes.CheckViolation, () => db.Execute("INSERT INTO ifood_order_snapshots(merchant_id,external_order_id,estabelecimento_id,order_type,payload,pending_amount)VALUES(@Merchant,@Order,@Store,'DELIVERY','{}',-1)", new { Merchant = db.MerchantA, Order = db.OrderA, Store = db.StoreA }));
        await Reject(PostgresErrorCodes.CheckViolation, () => db.Execute("UPDATE ifood_outbound_commands SET payload='[]'"));
    }

    [DeliveryDatabaseFact]
    public async Task RollbackPreservesLegacyRowsAndRefusesToDiscardIntegrationData()
    {
        await using var db = await Database.Create(apply: false);
        await db.Migrate(SchemaFile);
        await db.Migrate(VerifyFile);
        await db.Rollback();
        await db.Rollback();
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_tracking_schema_versions"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido WHERE legacy_value='preservado'"));
        await db.Migrate(SchemaFile);
        await db.Execute("INSERT INTO ifood_connections(merchant_id,estabelecimento_id)VALUES(@Merchant,@Store)", new { Merchant = db.MerchantA, Store = db.StoreA });
        await Assert.ThrowsAsync<PostgresException>(() => db.Rollback());
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_connections"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido"));
    }

    private static async Task Reject(string sqlState, Func<Task> action)
        => Assert.Equal(sqlState, (await Assert.ThrowsAsync<PostgresException>(action)).SqlState);

    private sealed class Database : IAsyncDisposable
    {
        private readonly string _schema = "ifood_schema_" + Guid.NewGuid().ToString("N");
        private string _baseConnection = "";
        private NpgsqlDataSource _source = null!;
        public Guid StoreA { get; } = Guid.NewGuid();
        public Guid StoreB { get; } = Guid.NewGuid();
        public Guid MerchantA { get; } = Guid.NewGuid();
        public Guid MerchantB { get; } = Guid.NewGuid();
        public Guid OrderA { get; } = Guid.NewGuid();
        public Guid OrderB { get; } = Guid.NewGuid();

        public static async Task<Database> Create(bool apply = true)
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Testes iFood exigem PostgreSQL local isolado.");
            var db = new Database { _baseConnection = options.ConnectionString };
            await using (var admin = new NpgsqlConnection(db._baseConnection))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE SCHEMA {db._schema}");
            }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);
            try
            {
                await db.Execute("""
CREATE TABLE estabelecimentos(id UUID PRIMARY KEY);
CREATE TABLE pedido(id INTEGER PRIMARY KEY,id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos(id),legacy_value TEXT);
CREATE TABLE delivery_tracking_schema_versions(version TEXT PRIMARY KEY);
INSERT INTO estabelecimentos VALUES(@StoreA),(@StoreB);
INSERT INTO pedido VALUES(1,@StoreA,'preservado'),(2,@StoreB,'preservado');
""", new { db.StoreA, db.StoreB });
                if (apply)
                {
                    await db.Migrate(SchemaFile);
                    await db.Execute("INSERT INTO ifood_connections(merchant_id,estabelecimento_id)VALUES(@MerchantA,@StoreA),(@MerchantB,@StoreB)", new { db.MerchantA, db.StoreA, db.MerchantB, db.StoreB });
                }
                return db;
            }
            catch { await db.DisposeAsync(); throw; }
        }

        public async Task Execute(string sql, object? args = null)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, args);
        }
        public async Task<T> Scalar<T>(string sql)
        {
            await using var connection = await _source.OpenConnectionAsync();
            return (await connection.ExecuteScalarAsync<T>(sql))!;
        }
        public async Task Migrate(string file) => await Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", file)));
        public async Task Rollback() => await Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261013_01_ifood_integration_schema.down.sql")));
        public Task Link(Guid merchant, Guid store, int pedido, Guid order) => Execute("INSERT INTO ifood_order_links(merchant_id,external_order_id,estabelecimento_id,pedido_id,display_number)VALUES(@Merchant,@Order,@Store,@Pedido,'001234')", new { Merchant = merchant, Store = store, Pedido = pedido, Order = order });
        public Task Command(Guid operation, Guid merchant, Guid store, Guid order, Guid? parent = null) => Execute("""
INSERT INTO ifood_outbound_commands(operation_id,merchant_id,external_order_id,estabelecimento_id,command_type,payload,payload_hash,expected_order_revision,parent_operation_id)
VALUES(@Operation,@Merchant,@Order,@Store,'confirm','{}',@Hash,1,@Parent)
""", new { Operation = operation, Merchant = merchant, Order = order, Store = store, Hash = new string('a', 64), Parent = parent });

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await using var admin = new NpgsqlConnection(_baseConnection);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
