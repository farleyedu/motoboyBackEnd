using APIBack.DTOs.Configuracoes;
using APIBack.Infrastructure;
using APIBack.Repository;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed class StoreOperationDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task OpensOnlyTheLocalDayClosesAtTheExactTimeAndKeepsWeeklySchedule()
    {
        await using var db = await Database.Create();
        db.Clock.Now = DateTimeOffset.Parse("2026-10-10T02:30:00Z"); // 09/10, 23h30 no Brasil.
        var opened = await db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request("23:45"));
        Assert.True(opened.AbertoAgora);
        Assert.Equal("2026-10-09", opened.DataLocal);
        Assert.Equal("especial", opened.Origem);
        Assert.Equal(7, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario WHERE fechado"));
        Assert.Equal(new TimeSpan(23, 45, 0), await db.Hours.ObterHoraFechamentoAsync(db.StoreId, new DateOnly(2026, 10, 9)));
        db.Clock.Now = DateTimeOffset.Parse("2026-10-10T02:44:59Z");
        Assert.True(await db.Hours.EstaAbertoAgoraAsync(db.StoreId, db.Clock.Now, "America/Sao_Paulo"));
        db.Clock.Now = DateTimeOffset.Parse("2026-10-10T02:45:00Z");
        Assert.False((await db.Hours.ObterEstadoAsync(db.StoreId)).AbertoAgora);
        Assert.False(await db.Hours.EstaAbertoAgoraAsync(db.StoreId, db.Clock.Now, "America/Sao_Paulo"));
        db.Clock.Now = DateTimeOffset.Parse("2026-10-10T15:00:00Z");
        Assert.Equal("semanal", (await db.Hours.ObterEstadoAsync(db.StoreId)).Origem);
        Assert.False((await db.Hours.ObterEstadoAsync(db.StoreId)).AbertoAgora);
    }

    [DeliveryDatabaseFact]
    public async Task PublicMenuAttendanceAndOrderRulesSeeTheSameDailyOpening()
    {
        await using var db = await Database.Create();
        var attendance = new AtendimentoRepository(db.Source, db.Clock);
        var publicOrders = new PedidosAbertosService(attendance, NullLogger<PedidosAbertosService>.Instance, db.Clock);
        Assert.False((await publicOrders.AvaliarAsync(db.StoreId, true)).Aberto);
        await db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request("14:00"));
        Assert.True((await attendance.GetConfigAsync(db.StoreId)).AbertoAgora);
        Assert.True((await publicOrders.AvaliarAsync(db.StoreId, true)).Aberto);
        Assert.False((await publicOrders.AvaliarAsync(db.StoreId, false)).Aberto); // Uma pausa explícita ainda vale.
        db.Clock.Now = DateTimeOffset.Parse("2026-10-09T17:00:00Z");
        Assert.False((await attendance.GetConfigAsync(db.StoreId)).AbertoAgora);
        Assert.False((await publicOrders.AvaliarAsync(db.StoreId, true)).Aberto);
    }

    [DeliveryDatabaseFact]
    public async Task ResumesPausedOrdersAndTwoConcurrentOpensDoNotReplaceTheFirstClosingTime()
    {
        await using var db = await Database.Create();
        await db.Execute("UPDATE estabelecimentos SET aceita_pedidos = FALSE");
        var results = await Task.WhenAll(db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request("14:00")), db.Hours.AbrirHojeAsync(db.StoreId, 8, db.Request("15:00")));
        Assert.All(results, r => Assert.True(r.AbertoAgora && r.AceitaPedidos));
        Assert.Equal(results[0].FechaAs, results[1].FechaAs);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario_especial"));
    }

    [DeliveryDatabaseFact]
    public async Task RejectsWrongStoreChangedDayAndInvalidOrPastClosingWithoutChangingTheStore()
    {
        await using var db = await Database.Create();
        var wrongStore = db.Request("14:00"); wrongStore.EstabelecimentoId = Guid.NewGuid();
        Assert.Equal("ESTABLISHMENT_CHANGED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Hours.AbrirHojeAsync(db.StoreId, 7, wrongStore))).Code);
        var yesterday = db.Request("14:00"); yesterday.DataLocal = "2026-10-08";
        Assert.Equal("DAY_CHANGED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Hours.AbrirHojeAsync(db.StoreId, 7, yesterday))).Code);
        foreach (var time in new[] { "11:59", "12:00", "24:00", "2:00", "25:00", "tomorrow", "14:00:00" })
            Assert.Equal("INVALID_CLOSING_TIME", (await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request(time)))).Code);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario_especial"));
        Assert.False((await db.Hours.ObterEstadoAsync(db.StoreId)).AbertoAgora);
    }

    [DeliveryDatabaseFact]
    public async Task ReplacesOnlyTodaysExceptionAndLeavesOtherStoresAndFutureDatesAlone()
    {
        await using var db = await Database.Create();
        var other = Guid.NewGuid();
        await db.Execute(@"
INSERT INTO estabelecimentos(id, aceita_pedidos) VALUES (@Other, FALSE);
INSERT INTO estabelecimento_horario_especial(estabelecimento_id, data, fechado) VALUES
 (@Id, '2026-10-09', TRUE), (@Id, '2026-10-10', TRUE), (@Other, '2026-10-09', TRUE);", new { Id = db.StoreId, Other = other });
        await db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request("14:00"));
        Assert.Equal(3, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario_especial"));
        Assert.Equal(2, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario_especial WHERE fechado"));
        Assert.False((await db.Hours.ObterEstadoAsync(other)).AceitaPedidos);
    }

    [DeliveryDatabaseFact]
    public async Task CancellingWhileAnotherWriterLocksTheStoreDoesNotOpenIt()
    {
        await using var db = await Database.Create();
        await using var conn = await db.Source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("SELECT id FROM estabelecimentos FOR UPDATE", transaction: tx);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Hours.AbrirHojeAsync(db.StoreId, 7, db.Request("14:00"), cancelled.Token));
        await tx.RollbackAsync();
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM estabelecimento_horario_especial"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-09T15:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Database : IAsyncDisposable
    {
        public Guid StoreId { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public NpgsqlDataSource Source { get; private set; } = null!;
        public HorarioOperacaoRepository Hours => new(Source, Clock);
        private readonly string _schema = "store_operation_" + Guid.NewGuid().ToString("N");
        private string _baseConnection = "";
        public OpenStoreTodayRequest Request(string close) => new() { EstabelecimentoId = StoreId, DataLocal = AtendimentoConfigRules.ParaHorarioLocal(Clock.Now.UtcDateTime).ToString("yyyy-MM-dd"), FechaAs = close };
        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not "127.0.0.1" and not "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local para estes testes.");
            var db = new Database { _baseConnection = options.ConnectionString };
            await using (var admin = new NpgsqlConnection(db._baseConnection)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            DapperConfiguration.Configure();
            await db.Execute(@"
CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, aceita_pedidos bool, data_atualizacao timestamptz);
CREATE TABLE estabelecimento_horario(id bigserial PRIMARY KEY, estabelecimento_id uuid REFERENCES estabelecimentos(id), dia_semana smallint, fechado bool, abre_as time, fecha_as time, UNIQUE(estabelecimento_id, dia_semana));
CREATE TABLE estabelecimento_horario_especial(id bigserial PRIMARY KEY, estabelecimento_id uuid REFERENCES estabelecimentos(id), data date, fechado bool, abre_as time, fecha_as time, motivo varchar(120), UNIQUE(estabelecimento_id, data));
INSERT INTO estabelecimentos(id, aceita_pedidos) VALUES(@Id, TRUE);
INSERT INTO estabelecimento_horario(estabelecimento_id, dia_semana, fechado) SELECT @Id, dia, TRUE FROM generate_series(0, 6) dia;", new { Id = db.StoreId });
            return db;
        }
        public async Task Execute(string sql, object? args = null) { await using var c = await Source.OpenConnectionAsync(); await c.ExecuteAsync(sql, args); }
        public async Task<T> Scalar<T>(string sql) { await using var c = await Source.OpenConnectionAsync(); return (await c.ExecuteScalarAsync<T>(sql))!; }
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var admin = new NpgsqlConnection(_baseConnection); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
