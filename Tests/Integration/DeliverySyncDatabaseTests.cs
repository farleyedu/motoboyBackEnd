using APIBack.Hubs;
using APIBack.Infrastructure;
using APIBack.Middleware;
using APIBack.Model.Auth;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository;
using APIBack.Service;
using APIBack.Service.Interface;
using APIBack.Services;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed class DeliveryDatabaseFactAttribute : FactAttribute
{
    public DeliveryDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")))
            Skip = "Defina TEST_DELIVERY_DATABASE com um PostgreSQL local para executar a integracao.";
    }
}

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task BatchStoresTwentyPointsAndOneEventAndReplaysWithoutAnotherWrite()
    {
        await using var db = await Database.Create();
        var locations = Enumerable.Range(1, 20).Select(Database.Point).ToArray();
        var result = await db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, locations);
        Assert.All(result, r => Assert.Equal(2, r.SessionVersion));
        Assert.Single(result.Where(r => r.UpdatedCurrent));
        Assert.Equal(20, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_samples"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Equal(20, await db.Scalar<long>("SELECT sequence FROM motoboy_location_current"));
        var replay = await db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, locations);
        Assert.All(replay, r => Assert.Equal("duplicate", r.Outcome));
        Assert.Equal(result[0].ReceivedAtUtc, replay[0].ReceivedAtUtc);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
        Assert.Equal(21, (await db.Repository.HeartbeatAsync(db.SessionId, 1, 4)).NextLocationSequence);
        locations[19].PayloadHash = "conflict";
        Assert.Equal("SEQUENCE_CONFLICT", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, new[] { Database.Point(21), locations[19] }))).Code);
        Assert.Equal(20, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_samples"));
    }

    [DeliveryDatabaseFact]
    public async Task StaleBatchIsAcknowledgedButLegacyEndpointRetainsConflict()
    {
        await using var db = await Database.Create();
        await db.Repository.WriteLocationAsync(db.SessionId, 1, 4, Database.Point(10));
        var old = Database.Point(5);
        Assert.Equal("stale", (await db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, new[] { old }))[0].Outcome);
        Assert.Equal("STALE_SEQUENCE", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.WriteLocationAsync(db.SessionId, 1, 4, old))).Code);
        Assert.Equal(10, await db.Scalar<long>("SELECT sequence FROM motoboy_location_current"));
    }

    [DeliveryDatabaseFact]
    public async Task HeartbeatReturnsUpdatedSessionAndRejectsRevokedMembershipAndWrongEpoch()
    {
        await using var db = await Database.Create();
        var session = await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        Assert.Equal(2, session.Version); Assert.Equal("Teste", session.Nome); Assert.Equal(7, session.UsuarioId);
        Assert.InRange((session.ExpiresAtUtc - session.LastHeartbeatAtUtc).TotalSeconds, 89, 91);
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.HeartbeatAsync(db.SessionId, 1, 99))).Code);
        await db.Execute("UPDATE motoboy_estabelecimento SET ativo = FALSE");
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.HeartbeatAsync(db.SessionId, 1, 4))).Code);
        Assert.Equal("LINK_FORBIDDEN", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.WriteLocationAsync(db.SessionId, 1, 4, Database.Point(1)))).Code);
    }

    [DeliveryDatabaseFact]
    public async Task RequestCancellationInterruptsSessionLockWaitAndDoesNotPublish()
    {
        await using var db = await Database.Create();
        await using var blocker = await db.Source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("SELECT session_id FROM motoboy_active_sessions FOR UPDATE", transaction: transaction);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Repository.HeartbeatAsync(db.SessionId, 1, 4, cancel.Token));
        await transaction.RollbackAsync();
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }

    [DeliveryDatabaseFact]
    public async Task ExpiredPublisherCannotAcknowledgeOrReleaseNewOwnersLease()
    {
        await using var db = await Database.Create(); var repo = new DeliveryOutboxRepository(db.Source);
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var records = await repo.ClaimAsync(a, 60, 100, default); Assert.Single(records!);
        Assert.Null(await repo.ClaimAsync(b, 60, 100, default));
        await db.Execute("UPDATE delivery_outbox_publisher_lease SET expires_at_utc = NOW() - INTERVAL '1 second'");
        Assert.Single((await repo.ClaimAsync(b, 60, 100, default))!);
        Assert.False(await repo.CompleteAsync(a, records![0].EventId, null, default));
        Assert.False(await repo.RenewAsync(a, 60, default));
        await repo.ReleaseAsync(a, default);
        Assert.Equal(b, await db.Scalar<Guid>("SELECT lease_id FROM delivery_outbox_publisher_lease"));
        Assert.True(await repo.CompleteAsync(b, records[0].EventId, null, default));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox WHERE published_at_utc IS NOT NULL"));
    }

    [DeliveryDatabaseFact]
    public async Task SignalRSendHasNoDatabaseConnectionCheckedOutAndAckUsesSeparateTransaction()
    {
        await using var db = await Database.Create(maxPoolSize: 1);
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        var hub = new Mock<IHubContext<DeliveryHub>>(); var clients = new Mock<IHubClients>(); var proxy = new Mock<IClientProxy>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object); clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, object?[] _, CancellationToken ct) => {
                // Pool de UMA conexao: falharia por timeout se o publicador ainda segurasse a conexao.
                await using var available = await db.Source.OpenConnectionAsync(ct);
                Assert.Equal(1, await available.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)));
            });
        var publisher = new DeliveryOutboxPublisher(new DeliveryOutboxRepository(db.Source), hub.Object,
            Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions()), NullLogger<DeliveryOutboxPublisher>.Instance);
        await publisher.PublishBatchAsync(default);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox WHERE published_at_utc IS NOT NULL"));
    }

    [DeliveryDatabaseFact]
    public async Task ConcurrentHeartbeatAndGpsDoNotLoseVersionsOrHistory()
    {
        await using var db = await Database.Create();
        var heartbeats = Enumerable.Range(1, 30).Select(_ => db.Repository.HeartbeatAsync(db.SessionId, 1, 4)).ToArray();
        var gps = db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, Enumerable.Range(1, 20).Select(Database.Point).ToArray());
        var sessions = await Task.WhenAll(heartbeats); await gps;
        Assert.Equal(30, sessions.Select(s => s.Version).Distinct().Count());
        Assert.Equal(32, await db.Scalar<long>("SELECT version FROM motoboy_active_sessions"));
        Assert.Equal(20, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_samples"));
        Assert.Equal(31, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }

    [DeliveryDatabaseFact]
    public async Task QueueCancellationInterruptsDatabaseRead()
    {
        await using var db = await Database.Create();
        await db.Execute("CREATE TABLE delivery_motoboy_route(motoboy_id int, estabelecimento_id uuid, version bigint)");
        await using var blocker = await db.Source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("LOCK TABLE delivery_motoboy_route IN ACCESS EXCLUSIVE MODE", transaction: transaction);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PedidoQueueRepository(db.Source).GetQueueAsync(db.StoreId, 1, cancel.Token));
        await transaction.RollbackAsync();
    }

    [DeliveryDatabaseFact]
    public async Task SimulatorHeartbeatKeepsItsSeparatePresencePolicy()
    {
        await using var db = await Database.Create();
        await db.Execute("UPDATE motoboy SET is_simulated = TRUE; UPDATE motoboy_active_sessions SET origin = 'simulator', id_usuario = NULL;");
        var session = await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        Assert.Null(session.UsuarioId); Assert.Equal("simulator", session.Origin);
        Assert.InRange((session.ExpiresAtUtc - session.LastHeartbeatAtUtc).TotalDays, 29.99, 30.01);
    }

    [DeliveryDatabaseFact]
    public async Task QueueSnapshotStillIncludesOrderDetailsPoliciesAndRouteState()
    {
        await using var db = await Database.Create();
        await db.Execute(@"
CREATE TABLE delivery_motoboy_route(motoboy_id int, estabelecimento_id uuid, version bigint, route_state text, returning_since_utc timestamptz);
CREATE TABLE delivery_route_stops(id bigint, pedido_id int, motoboy_id int, estabelecimento_id uuid, position int, stop_status text,
 assigned_at_utc timestamptz, picked_up_at_utc timestamptz, arrived_at_utc timestamptz, locked bool, offer_id uuid, offered_at_utc timestamptz);
CREATE TABLE delivery_settings(estabelecimento_id uuid, transfer_policy text, require_delivery_code bool, allow_motoboy_reorder bool,
 allow_motoboy_refuse bool, updated_at_utc timestamptz, store_return_radius_m int, require_motoboy_acceptance bool, offer_timeout_minutes int);
CREATE TABLE pedido(id int, nome_cliente text, telefone_cliente text, endereco_entrega text, entrega_rua text, entrega_numero text,
 entrega_bairro text, entrega_cidade text, entrega_estado text, entrega_cep text, latitude text, longitude text, items text, value text,
 tipo_pagamento text, status_pagamento text, troco text, observacoes text, previsao_entrega text, data_pedido text, codigo_entrega text);
INSERT INTO delivery_motoboy_route VALUES(1,@StoreId,3,'delivering',NULL);
INSERT INTO delivery_settings VALUES(@StoreId,'direct',TRUE,TRUE,TRUE,NOW(),100,FALSE,5);
INSERT INTO pedido(id,nome_cliente,latitude,longitude,codigo_entrega) VALUES(23,'Cliente','-23.5','-46.6','1234'),(24,'Proximo',NULL,NULL,NULL);
INSERT INTO delivery_route_stops(id,pedido_id,motoboy_id,estabelecimento_id,position,stop_status,assigned_at_utc,locked)
VALUES(1,23,1,@StoreId,1,'en_route',NOW(),FALSE),(2,24,1,@StoreId,2,'assigned',NOW(),TRUE);", new { db.StoreId });
        var queue = await new PedidoQueueRepository(db.Source).GetQueueAsync(db.StoreId, 1);
        Assert.Equal(3, queue.Version); Assert.Equal("delivering", queue.RouteState);
        Assert.Equal(23, queue.Current!.PedidoId); Assert.Equal("Cliente", queue.Current.Pedido!.NomeCliente);
        Assert.True(queue.Current.Pedido.RequerCodigoEntrega); Assert.Single(queue.Next); Assert.True(queue.Next.Single().Locked);
    }

    [DeliveryDatabaseFact]
    public async Task ExpiredSessionDoesNotAcceptHeartbeatOrBatch()
    {
        await using var db = await Database.Create();
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc = NOW() - INTERVAL '1 second'");
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.HeartbeatAsync(db.SessionId, 1, 4))).Code);
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.WriteLocationsAsync(db.SessionId, 1, 4, new[] { Database.Point(1) }))).Code);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox"));
    }

    [DeliveryDatabaseFact]
    public async Task RetryDelayKeepsLaterEventsForSameAggregatePending()
    {
        await using var db = await Database.Create();
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        var repo = new DeliveryOutboxRepository(db.Source); var lease = Guid.NewGuid();
        var records = await repo.ClaimAsync(lease, 60, 100, default);
        Assert.True(await repo.CompleteAsync(lease, records![0].EventId, "offline", default));
        await repo.ReleaseAsync(lease, default);
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        var another = Guid.NewGuid();
        Assert.Empty((await repo.ClaimAsync(another, 60, 100, default))!);
        await repo.ReleaseAsync(another, default);
    }

    [DeliveryDatabaseFact]
    public async Task MiddlewareRechecksTenantAndRevocationAgainstLiveDatabase()
    {
        await using var db = await Database.Create();
        using var services = new ServiceCollection().AddSingleton(db.Source).BuildServiceProvider();
        var payload = new JwtPayload { MotoboySessionId = db.SessionId, MotoboyId = 1, SessionEpoch = 4, EstabelecimentoId = db.StoreId };
        var jwt = new Mock<IJwtService>(); jwt.Setup(j => j.ValidateToken("a.b.c")).Returns(payload);
        async Task<bool> Allowed()
        {
            var context = new DefaultHttpContext { RequestServices = services }; context.Request.Headers.Authorization = "Bearer a.b.c";
            await new JwtAuthenticationMiddleware(_ => Task.CompletedTask).InvokeAsync(context, jwt.Object, new ConfigurationBuilder().Build());
            return context.Items.ContainsKey("JwtPayload");
        }
        Assert.True(await Allowed()); payload.EstabelecimentoId = Guid.NewGuid(); Assert.False(await Allowed());
        payload.EstabelecimentoId = db.StoreId; await db.Execute("UPDATE motoboy_active_sessions SET revoked_at = NOW()");
        Assert.False(await Allowed());
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public OperationalSessionRepository Repository => new(Source, Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions { Enabled = true }));
        public Guid SessionId { get; } = Guid.NewGuid();
        public Guid StoreId { get; } = Guid.NewGuid();
        private string _schema = "delivery_sync_" + Guid.NewGuid().ToString("N");
        private string _baseConnection = "";
        public static async Task<Database> Create(int maxPoolSize = 5)
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host != "127.0.0.1" && options.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local para estes testes.");
            var db = new Database { _baseConnection = options.ConnectionString };
            await using (var admin = new NpgsqlConnection(db._baseConnection)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema; options.MaxPoolSize = maxPoolSize;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await db.Execute(@"
CREATE TABLE motoboy(id int PRIMARY KEY, nome text, avatar text, status int, canonical_motoboy_id int, is_simulated bool, id_usuario int);
CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, ativo bool, status text);
CREATE TABLE motoboy_estabelecimento(motoboy_id int, estabelecimento_id uuid, ativo bool, simulator_enabled bool);
CREATE TABLE usuario_estabelecimentos(id_usuario int, id_estabelecimento uuid, tipo_acesso text, ativo bool, status text);
CREATE TABLE motoboy_active_sessions(session_id uuid PRIMARY KEY, session_epoch bigint, motoboy_id int, id_usuario int,
 id_estabelecimento uuid, origin text, device_type text DEFAULT 'mobile', client_instance_id text, started_by_user_id int, idempotency_key uuid,
 started_at_utc timestamptz, last_heartbeat_at_utc timestamptz, expires_at_utc timestamptz,
 ended_at_utc timestamptz, revoked_at timestamptz, end_reason text, revoke_reason text, last_seen_at timestamptz, version bigint,paused_at_utc timestamptz);
CREATE TABLE motoboy_location_samples(id bigserial PRIMARY KEY, sample_id uuid, session_id uuid, motoboy_id int, estabelecimento_id uuid,
 sequence bigint, latitude double precision, longitude double precision, accuracy_meters double precision, speed_mps double precision,
 heading_degrees double precision, tracking_mode text, quality text, captured_at_utc timestamptz, received_at_utc timestamptz,
 payload_hash text, updated_current bool, UNIQUE(session_id,sample_id), UNIQUE(session_id,sequence));
CREATE TABLE motoboy_location_current(session_id uuid PRIMARY KEY, motoboy_id int, estabelecimento_id uuid, sample_id uuid,
 sequence bigint, latitude double precision, longitude double precision, accuracy_meters double precision, speed_mps double precision,
 heading_degrees double precision, tracking_mode text, quality text, captured_at_utc timestamptz, received_at_utc timestamptz, version bigint);
CREATE TABLE delivery_realtime_outbox(event_id uuid PRIMARY KEY, event_name text, target_group text, estabelecimento_id uuid,
 motoboy_id int, session_id uuid, session_epoch bigint, aggregate_version bigint, payload jsonb, occurred_at_utc timestamptz DEFAULT NOW(),
 published_at_utc timestamptz, attempts int DEFAULT 0, last_error text, next_attempt_at_utc timestamptz DEFAULT NOW());
CREATE TABLE delivery_tracking_schema_versions(version text PRIMARY KEY);
INSERT INTO motoboy VALUES(1,'Teste',NULL,2,NULL,FALSE,7);
INSERT INTO estabelecimentos VALUES(@StoreId,TRUE,'ativo');
INSERT INTO motoboy_estabelecimento VALUES(1,@StoreId,TRUE,TRUE);
INSERT INTO usuario_estabelecimentos VALUES(7,@StoreId,'motoboy',TRUE,'ativo');
INSERT INTO motoboy_active_sessions(session_id,session_epoch,motoboy_id,id_usuario,id_estabelecimento,origin,started_by_user_id,
 started_at_utc,last_heartbeat_at_utc,expires_at_utc,version) VALUES(@SessionId,4,1,7,@StoreId,'mobile',7,NOW(),NOW(),NOW()+INTERVAL '90 seconds',1);",
                new { db.StoreId, db.SessionId });
            await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261007_01_outbox_publisher_lease.sql")));
            return db;
        }
        public async Task Execute(string sql, object? parameters = null) { await using var c = await Source.OpenConnectionAsync(); await c.ExecuteAsync(sql, parameters); }
        public async Task<T> Scalar<T>(string sql) { await using var c = await Source.OpenConnectionAsync(); return (await c.ExecuteScalarAsync<T>(sql))!; }
        public static OperationalLocationWrite Point(int sequence) => new() { SampleId = Guid.NewGuid(), Sequence = sequence,
            Latitude = -23.5, Longitude = -46.6, CapturedAtUtc = DateTimeOffset.UtcNow, PayloadHash = "hash-" + sequence };
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var admin = new NpgsqlConnection(_baseConnection); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
