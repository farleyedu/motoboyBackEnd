using APIBack.Atendimento.Templates;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Roda so com TEST_DELIVERY_DATABASE (PostgreSQL local, schema proprio descartado ao final).</summary>
public sealed class WhatsappTemplatesDatabaseTests
{
    private const string Waba = "200000000000001";

    private static TemplateWhatsapp Novo(Guid loja, string nome = "pedido_ok", string situacao = SituacaoTemplate.Enviando, string? metaId = null) => new()
    {
        Id = Guid.NewGuid(), IdEstabelecimento = loja, WabaId = Waba, Nome = nome, Idioma = "pt_BR", Categoria = "UTILITY",
        ConteudoJson = "{\"corpo\":\"Ola\"}", Situacao = situacao, MetaTemplateId = metaId, CriadoPor = 1, EnviadoEm = DateTime.UtcNow
    };

    [DeliveryDatabaseFact]
    public async Task MigrationIsIdempotentAndUniqueIndexesBlockDuplicateSubmissions()
    {
        await using var db = await Database.Create();
        await db.Execute(Database.Sql("20261029_01_whatsapp_templates.sql"));
        await db.Execute(Database.Sql("20261029_02_whatsapp_templates_verify.sql"));

        var a = Novo(db.LojaA);
        await db.Repo.InserirAsync(a);
        // Segundo envio igual enquanto o primeiro esta em curso: o indice barra.
        await Assert.ThrowsAsync<TemplateConflitoException>(() => db.Repo.InserirAsync(Novo(db.LojaA)));

        // Falha nao bloqueia o nome; o historico fica.
        a.Situacao = SituacaoTemplate.Falha;
        await db.Repo.AtualizarAsync(a);
        var b = Novo(db.LojaA);
        await db.Repo.InserirAsync(b);
        b.Situacao = SituacaoTemplate.Enviado;
        b.MetaTemplateId = "901";
        b.StatusMeta = "APPROVED";
        await db.Repo.AtualizarAsync(b);

        // O mesmo ID da Meta nao pertence a duas lojas.
        var c = Novo(db.LojaB, "outro_nome");
        await db.Repo.InserirAsync(c);
        c.MetaTemplateId = "901";
        c.Situacao = SituacaoTemplate.Enviado;
        await Assert.ThrowsAsync<TemplateConflitoException>(() => db.Repo.AtualizarAsync(c));

        // "enviado" sem ID da Meta e recusado pelo banco.
        await Assert.ThrowsAsync<PostgresException>(() => db.Repo.InserirAsync(Novo(db.LojaA, "sem_id", SituacaoTemplate.Enviado)));

        var daLojaA = await db.Repo.ListarAsync(db.LojaA, Guid.NewGuid(), Waba);
        Assert.Equal(2, daLojaA.Count);
        Assert.All(daLojaA, t => Assert.Equal(db.LojaA, t.IdEstabelecimento));
        Assert.Equal(3, (await db.Repo.ListarDaWabaAsync(Waba)).Count);

        await db.Repo.RegistrarEventoAsync(b.Id, "enviado", 1, "{\"status\":\"APPROVED\"}");
        Assert.Equal("enviado", Assert.Single(await db.Repo.ListarEventosAsync(b.Id)).Acao);

        await db.Execute(Database.Sql("20261029_01_whatsapp_templates.sql"));
        Assert.Equal(3L, await db.Scalar<long>("SELECT count(*) FROM whatsapp_template"));

        await db.Execute(Database.Sql("rollback/20261029_01_whatsapp_templates.down.sql"));
        Assert.Null(await db.Scalar<string?>("SELECT to_regclass('whatsapp_template')::text"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM canal_whatsapp"));
    }

    private sealed class Database : IAsyncDisposable
    {
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        public SqlTemplateRepository Repo { get; private set; } = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "wa_templates_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static string Sql(string nome) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", nome));

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);
            db.Repo = new SqlTemplateRepository(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = options.ConnectionString }).Build());
            await db.Execute(@"
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY);
CREATE TABLE canal_whatsapp (id uuid PRIMARY KEY, id_estabelecimento uuid NOT NULL REFERENCES estabelecimentos (id), waba_id text NULL);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
INSERT INTO estabelecimentos VALUES (@A), (@B);
INSERT INTO canal_whatsapp VALUES (gen_random_uuid(), @A, NULL);", new { A = db.LojaA, B = db.LojaB });
            return db;
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await _source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T?> Scalar<T>(string sql) { await using var cx = await _source.OpenConnectionAsync(); return await cx.ExecuteScalarAsync<T>(sql); }

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
