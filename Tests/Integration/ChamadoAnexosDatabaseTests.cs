using APIBack.Infrastructure;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SkiaSharp;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Roda so com TEST_DELIVERY_DATABASE (PostgreSQL local, schema proprio descartado ao final).</summary>
public sealed class ChamadoAnexosDatabaseTests
{
    private static byte[] Png(int largura = 40, int altura = 30)
    {
        using var bitmap = new SKBitmap(largura, altura);
        bitmap.Erase(SKColors.Teal);
        using var imagem = SKImage.FromBitmap(bitmap);
        return imagem.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static ArquivoEnviado Arquivo(string nome, byte[]? bytes = null)
    {
        bytes ??= Png();
        return new ArquivoEnviado(nome, "image/png", bytes.Length, () => new MemoryStream(bytes));
    }

    [DeliveryDatabaseFact]
    public async Task OpeningWithImagesStoresThemOnTheFirstMessageAndTheyOutliveTheService()
    {
        await using var db = await Database.Create();
        var chamado = await db.Service.AbrirAsync(db.LojaA, 1, new("parou", "Impressora parou\nDesde cedo"), [Arquivo("a.png"), Arquivo("b.png")]);
        Assert.Equal("Impressora parou", chamado.Titulo);
        Assert.Equal(1, chamado.Mensagens);
        Assert.Equal(2, db.Storage.Objetos.Count);
        Assert.All(db.Storage.Objetos.Keys, id => Assert.StartsWith($"chamados/{db.LojaA}/{chamado.Id}/", id));

        // Servico novo (como depois de reiniciar o backend): tudo vem do banco.
        var (_, mensagens) = await db.NovoServico().ObterAsync(chamado.Id, db.LojaA);
        var primeira = Assert.Single(mensagens);
        Assert.Equal(["a.png", "b.png"], primeira.Anexos.Select(a => a.Nome));
        Assert.All(primeira.Anexos, a => { Assert.Equal("image/png", a.Tipo); Assert.StartsWith("https://", a.Url); Assert.True(a.Bytes > 0); });
        var (_, daGestao) = await db.Service.ObterAsync(chamado.Id, null);
        Assert.Equal(2, daGestao.Single().Anexos.Count);
    }

    [DeliveryDatabaseFact]
    public async Task TicketsWithoutImagesBehaveAsBeforeAndRepliesCarryTheirOwnImages()
    {
        await using var db = await Database.Create();
        var chamado = await db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Como faço isso?"));
        Assert.Empty(db.Storage.Objetos);
        await db.Service.ResponderAsync(chamado.Id, null, 9, true, "Veja o print", [Arquivo("passo.png")]);
        await db.Service.ResponderAsync(chamado.Id, db.LojaA, 1, false, "", [Arquivo("erro.png")]);
        await db.Service.ResponderAsync(chamado.Id, db.LojaA, 1, false, "Só texto");
        var (dto, mensagens) = await db.Service.ObterAsync(chamado.Id, db.LojaA);
        Assert.Equal(4, mensagens.Count);
        Assert.Empty(mensagens[0].Anexos);
        Assert.Equal(["passo.png"], mensagens[1].Anexos.Select(a => a.Nome));
        Assert.True(mensagens[1].DaZippy);
        Assert.Equal(["erro.png"], mensagens[2].Anexos.Select(a => a.Nome));
        Assert.Equal("", mensagens[2].Texto);
        Assert.Empty(mensagens[3].Anexos);
        Assert.Equal("Só texto", dto.UltimaMensagem);
        await db.Service.ResponderAsync(chamado.Id, db.LojaA, 1, false, "", [Arquivo("ultimo.png")]);
        Assert.Equal("📷 Imagem", (await db.Service.ObterAsync(chamado.Id, db.LojaA)).Item1.UltimaMensagem);
    }

    [DeliveryDatabaseFact]
    public async Task AStoreNeverSeesOrWritesTheAttachmentsOfAnotherStore()
    {
        await using var db = await Database.Create();
        var chamado = await db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Chamado da loja A"), [Arquivo("segredo.png")]);
        var envios = db.Storage.Objetos.Count;
        Assert.Equal(404, (await Assert.ThrowsAsync<ChamadoException>(() => db.Service.ObterAsync(chamado.Id, db.LojaB))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ChamadoException>(() => db.Service.ResponderAsync(chamado.Id, db.LojaB, 2, false, "oi", [Arquivo("intruso.png")]))).Status);
        Assert.Equal(envios, db.Storage.Objetos.Count); // Nem subiu arquivo para um chamado que nao e dela.
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM chamado_anexo"));
        Assert.Empty(await db.Service.ListarAsync(db.LojaB));
    }

    [DeliveryDatabaseFact]
    public async Task AFailedUploadLeavesNoPartialTicketAndNoOrphanFiles()
    {
        await using var db = await Database.Create();
        db.Storage.FalharNoEnvioNumero = 1; // O segundo print falha.
        var ex = await Assert.ThrowsAsync<ChamadoException>(() => db.Service.AbrirAsync(db.LojaA, 1, new("parou", "Não consigo vender"), [Arquivo("a.png"), Arquivo("b.png")]));
        Assert.Equal(502, ex.Status);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado"));
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado_mensagem"));
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado_anexo"));
        Assert.Empty(db.Storage.Objetos);

        db.Storage.FalharNoEnvioNumero = -1;
        var chamado = await db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Pergunta"));
        db.Storage.FalharNoEnvioNumero = db.Storage.Envios;
        await Assert.ThrowsAsync<ChamadoException>(() => db.Service.ResponderAsync(chamado.Id, db.LojaA, 1, false, "com print", [Arquivo("c.png")]));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM chamado_mensagem"));
    }

    [DeliveryDatabaseFact]
    public async Task ADatabaseFailureAfterTheUploadRemovesTheUploadedFilesAndTheTicket()
    {
        await using var db = await Database.Create();
        await db.Execute("ALTER TABLE chamado_anexo ADD CONSTRAINT ck_teste_falha CHECK (nome <> 'falha.png')");
        await Assert.ThrowsAnyAsync<Exception>(() => db.Service.AbrirAsync(db.LojaA, 1, new("parou", "Vai falhar no banco"), [Arquivo("falha.png")]));
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado"));
        Assert.Empty(db.Storage.Objetos);
        Assert.Equal(1, db.Storage.Envios);

        var chamado = await db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Chamado normal"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Service.ResponderAsync(chamado.Id, null, 9, true, "resposta", [Arquivo("falha.png")]));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM chamado_mensagem"));
        Assert.Equal("aberto", (await db.Service.ObterAsync(chamado.Id, null)).Item1.Status); // O UPDATE da transacao tambem voltou.
        Assert.Empty(db.Storage.Objetos);
    }

    [DeliveryDatabaseFact]
    public async Task InvalidFilesAreRefusedBeforeAnyWrite()
    {
        await using var db = await Database.Create();
        var texto = new ArquivoEnviado("x.png", "image/png", 5, () => new MemoryStream("%PDF-"u8.ToArray()));
        Assert.Equal(422, (await Assert.ThrowsAsync<ChamadoException>(() => db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Com arquivo ruim"), [texto]))).Status);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado"));
        Assert.Equal(0, db.Storage.Envios);
    }

    [DeliveryDatabaseFact]
    public async Task MigrationIsIdempotentAndRollbackOnlyDropsTheAttachmentTable()
    {
        await using var db = await Database.Create();
        await db.Service.AbrirAsync(db.LojaA, 1, new("duvida", "Preservar"), [Arquivo("a.png")]);
        await db.Execute(Database.Sql("20261028_04_chamado_anexos.sql"));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM chamado_anexo"));
        await db.Execute(Database.Sql("rollback/20261028_04_chamado_anexos.down.sql"));
        Assert.Null(await db.Scalar<string>("SELECT to_regclass('chamado_anexo')::text"));
        // O rollback so tira a tabela de anexos: chamados e mensagens ficam.
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM chamado_mensagem"));
        await db.Execute(Database.Sql("rollback/20261028_04_chamado_anexos.down.sql"));
        await db.Execute(Database.Sql("20261028_04_chamado_anexos.sql"));
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM chamado_anexo"));
    }

    private sealed class FakeStorage : IChamadoAnexoStorage
    {
        public Dictionary<string, byte[]> Objetos { get; } = [];
        public int Envios { get; private set; }
        public int FalharNoEnvioNumero { get; set; } = -1;

        public Task<string> EnviarAsync(string publicId, string nomeArquivo, byte[] bytes, CancellationToken ct = default)
        {
            if (Envios == FalharNoEnvioNumero) throw new InvalidOperationException("Cloudinary fora do ar.");
            Envios++;
            Objetos[publicId] = bytes;
            return Task.FromResult($"https://res.cloudinary.com/zippy/image/upload/v1/{publicId}.png");
        }

        public Task ExcluirAsync(string publicId, CancellationToken ct = default) { Objetos.Remove(publicId); return Task.CompletedTask; }
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public FakeStorage Storage { get; } = new();
        public ChamadosService Service { get; private set; } = null!;
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        private readonly string _schema = "chamado_anexos_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public ChamadosService NovoServico() => new(Source, new ChamadoAnexoService(Storage, NullLogger<ChamadoAnexoService>.Instance));

        public static string Sql(string nome) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", nome));

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await db.Execute(@"
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@A, 'Loja A'), (@B, 'Loja B');
INSERT INTO usuario VALUES (1, 'Maria'), (2, 'João'), (9, 'Suporte');", new { A = db.LojaA, B = db.LojaB });
            await db.Execute(Sql("20261028_03_chamados.sql"));
            await db.Execute(Sql("20261028_04_chamado_anexos.sql"));
            db.Service = db.NovoServico();
            return db;
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T?> Scalar<T>(string sql) { await using var cx = await Source.OpenConnectionAsync(); return await cx.ExecuteScalarAsync<T>(sql); }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
