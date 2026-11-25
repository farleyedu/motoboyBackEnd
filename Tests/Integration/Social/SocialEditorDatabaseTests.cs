using System.Text.Json;
using APIBack.Infrastructure;
using APIBack.Social.Arquivos;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Social;

/// <summary>Roda só com TEST_DELIVERY_DATABASE (PostgreSQL local, schema próprio descartado ao final).</summary>
public sealed class SocialEditorDatabaseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();
    private static JsonElement Receita(long corte, string extra = "\"lay\":{\"modo\":\"cima\"}") => Json($$"""{"v":1,"corteId":{{corte}},{{extra}}}""");
    private static string Hash(char c) => new(c, 64);

    [DeliveryDatabaseFact]
    public async Task Exemplo_cria_seis_cortes_uma_vez_so_e_e_apagavel()
    {
        await using var db = await Database.Create();
        var exemplo = await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct);
        Assert.False(exemplo.JaExistia);
        Assert.Equal(6, exemplo.CorteIds.Count);
        var cortes = await db.Editor.CortesAsync(db.LojaA, exemplo.CorteIds, Ct);
        Assert.Equal("Explosão + risada forte", cortes[0].Titulo);
        Assert.Equal(40, cortes[0].Duracao);
        Assert.Equal(92, cortes[0].Nota);
        Assert.True(cortes[0].Exemplo);
        Assert.Equal("Assalto ao Fleeca com a galera", cortes[0].VideoTitulo);
        Assert.Null(cortes[0].ReceitaVersao);
        Assert.Contains("explosao", cortes[0].Dados.GetRawText());

        var de_novo = await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct);
        Assert.True(de_novo.JaExistia);
        Assert.Equal(exemplo.CorteIds, de_novo.CorteIds);

        await db.Editor.ApagarVideoAsync(db.LojaA, exemplo.VideoId, Ct);
        Assert.Empty(await db.Editor.CortesAsync(db.LojaA, exemplo.CorteIds, Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Receita_versionada_responde_409_com_a_atual_e_guarda_30_versoes()
    {
        await using var db = await Database.Create();
        var corte = (await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct)).CorteIds[0];

        var semReceita = await Assert.ThrowsAsync<SocialException>(() => db.Editor.ReceitaAsync(db.LojaA, corte, Ct));
        Assert.Equal(404, semReceita.Status);

        var v1 = await db.Editor.SalvarReceitaAsync(db.LojaA, 1, corte, new(0, Receita(corte)), Ct);
        Assert.Equal(1, v1.Versao);
        // Outra aba ainda acha que não há receita: 409 com a versão 1.
        var conflito = await Assert.ThrowsAsync<SocialException>(() =>
            db.Editor.SalvarReceitaAsync(db.LojaA, 2, corte, new(0, Receita(corte, "\"lay\":{\"modo\":\"lado\"}")), Ct));
        Assert.Equal(409, conflito.Status);
        Assert.Equal("CONFLITO_VERSAO", conflito.Codigo);
        var detalhes = JsonSerializer.Serialize(conflito.Detalhes);
        Assert.Contains("\"versao\":1", detalhes);
        Assert.Contains("cima", detalhes);

        for (var i = 1; i < 35; i++)
            Assert.Equal(i + 1, (await db.Editor.SalvarReceitaAsync(db.LojaA, 1, corte, new(i, Receita(corte, $"\"n\":{i}")), Ct)).Versao);
        var atual = await db.Editor.ReceitaAsync(db.LojaA, corte, Ct);
        Assert.Equal(35, atual.Versao);
        Assert.Equal(34, atual.Receita.GetProperty("n").GetInt32());
        Assert.Equal(35, (await db.Editor.CortesAsync(db.LojaA, new[] { corte }, Ct)).Single().ReceitaVersao);

        var historico = await db.Editor.HistoricoAsync(db.LojaA, corte, Ct);
        Assert.Equal(30, historico.Count);
        Assert.Equal(35, historico[0].Versao);
        Assert.Equal(6, historico[^1].Versao);
        Assert.Equal("Maria", historico[0].QuemNome);
        Assert.Equal(404, (await Assert.ThrowsAsync<SocialException>(() => db.Editor.VersaoAsync(db.LojaA, corte, 5, Ct))).Status);
        Assert.Equal(20, (await db.Editor.VersaoAsync(db.LojaA, corte, 21, Ct)).Receita.GetProperty("n").GetInt32());
    }

    [DeliveryDatabaseFact]
    public async Task Uma_loja_nao_le_nem_grava_corte_receita_ou_arquivo_da_outra()
    {
        await using var db = await Database.Create();
        var corte = (await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct)).CorteIds[0];
        await db.Editor.SalvarReceitaAsync(db.LojaA, 1, corte, new(0, Receita(corte)), Ct);
        var arquivo = await db.Arquivos.CriarAsync(db.LojaA, 1, new("imagem", "logo.png", "image/png", 100, Hash('a')), Ct);

        Assert.Empty(await db.Editor.CortesAsync(db.LojaB, new[] { corte }, Ct));
        async Task NaoAcha(Func<Task> f) => Assert.Equal(404, (await Assert.ThrowsAsync<SocialException>(f)).Status);
        await NaoAcha(() => db.Editor.ReceitaAsync(db.LojaB, corte, Ct));
        await NaoAcha(() => db.Editor.SalvarReceitaAsync(db.LojaB, 2, corte, new(1, Receita(corte)), Ct));
        await NaoAcha(() => db.Editor.SalvarDadosAsync(db.LojaB, corte, Json("{}"), Ct));
        await NaoAcha(() => db.Editor.HistoricoAsync(db.LojaB, corte, Ct));
        await NaoAcha(() => db.Editor.VersaoAsync(db.LojaB, corte, 1, Ct));
        var video = (await db.Editor.CortesAsync(db.LojaA, new[] { corte }, Ct))[0].VideoId;
        await NaoAcha(() => db.Editor.ApagarVideoAsync(db.LojaB, video, Ct));
        await NaoAcha(() => db.Arquivos.ObterAsync(db.LojaB, arquivo.ArquivoId, Ct));
        await NaoAcha(() => db.Arquivos.ConfirmarAsync(db.LojaB, arquivo.ArquivoId, Ct));
        await NaoAcha(() => db.Arquivos.ApagarAsync(db.LojaB, 1, arquivo.ArquivoId, Ct));
        Assert.Equal(1, (await db.Editor.ReceitaAsync(db.LojaA, corte, Ct)).Versao);
    }

    [DeliveryDatabaseFact]
    public async Task Row_level_security_esconde_a_outra_loja_mesmo_sem_filtro_na_consulta()
    {
        await using var db = await Database.Create();
        await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct);
        await db.Editor.CriarExemploAsync(db.LojaB, 2, Ct);
        var papel = "social_rls_" + Guid.NewGuid().ToString("N")[..12];
        await using var c = await db.Source.OpenConnectionAsync();
        try
        {
            await c.ExecuteAsync($"CREATE ROLE {papel} NOLOGIN; GRANT USAGE ON SCHEMA {db.Schema} TO {papel}; " +
                                 $"GRANT SELECT ON ALL TABLES IN SCHEMA {db.Schema} TO {papel};");
        }
        catch (PostgresException) { return; } // usuário do teste sem CREATEROLE: o filtro por loja já foi testado acima
        try
        {
            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync($"SET LOCAL ROLE {papel}", transaction: tx);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM social_cortes", transaction: tx));
            await c.ExecuteAsync("SELECT set_config('zippy.loja', @l, true)", new { l = db.LojaA.ToString() }, tx);
            Assert.Equal(6, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM social_cortes", transaction: tx));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM social_videos", transaction: tx));
            await tx.RollbackAsync();
        }
        finally
        {
            await c.ExecuteAsync($"DROP OWNED BY {papel}; DROP ROLE {papel};");
        }
    }

    [DeliveryDatabaseFact]
    public async Task Hash_repetido_devolve_o_mesmo_arquivo_sem_novo_envio()
    {
        await using var db = await Database.Create();
        var novo = await db.Arquivos.CriarAsync(db.LojaA, 1, new("imagem", "logo.png", "image/png", 100, Hash('b')), Ct);
        Assert.NotNull(novo.UrlEnviar);
        Assert.False(novo.JaExistia);

        // Antes de confirmar: o mesmo arquivo, com nova URL para terminar o envio.
        var pendente = await db.Arquivos.CriarAsync(db.LojaA, 1, new("imagem", "logo2.png", "image/png", 100, Hash('b')), Ct);
        Assert.Equal(novo.ArquivoId, pendente.ArquivoId);
        Assert.NotNull(pendente.UrlEnviar);

        var semEnvio = await Assert.ThrowsAsync<SocialException>(() => db.Arquivos.ConfirmarAsync(db.LojaA, novo.ArquivoId, Ct));
        Assert.Equal(400, semEnvio.Status);
        db.Storage.Enviar(novo.ArquivoId, 100);
        var confirmado = await db.Arquivos.ConfirmarAsync(db.LojaA, novo.ArquivoId, Ct);
        Assert.True(confirmado.Enviado);
        Assert.StartsWith("https://r2.teste/ler/social/", confirmado.UrlLer);

        var repetido = await db.Arquivos.CriarAsync(db.LojaA, 2, new("imagem", "outro.png", "image/png", 100, Hash('b')), Ct);
        Assert.Equal(novo.ArquivoId, repetido.ArquivoId);
        Assert.Null(repetido.UrlEnviar);
        Assert.True(repetido.JaExistia);

        var outraLoja = await db.Arquivos.CriarAsync(db.LojaB, 2, new("imagem", "logo.png", "image/png", 100, Hash('b')), Ct);
        Assert.NotEqual(novo.ArquivoId, outraLoja.ArquivoId);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_arquivos WHERE hash_sha256 = repeat('b', 64) AND estabelecimento_id = '" + db.LojaA + "'"));
    }

    [DeliveryDatabaseFact]
    public async Task So_o_dono_apaga_e_a_limpeza_tira_do_R2()
    {
        await using var db = await Database.Create();
        var a = await db.Arquivos.CriarAsync(db.LojaA, 1, new("audio", "fala.mp3", "audio/mpeg", 50, Hash('c'), "gravacao"), Ct);
        db.Storage.Enviar(a.ArquivoId, 50);
        await db.Arquivos.ConfirmarAsync(db.LojaA, a.ArquivoId, Ct);

        Assert.Equal(403, (await Assert.ThrowsAsync<SocialException>(() => db.Arquivos.ApagarAsync(db.LojaA, 2, a.ArquivoId, Ct))).Status);
        await db.Arquivos.ApagarAsync(db.LojaA, 1, a.ArquivoId, Ct);
        Assert.Equal(404, (await Assert.ThrowsAsync<SocialException>(() => db.Arquivos.ObterAsync(db.LojaA, a.ArquivoId, Ct))).Status);

        // Envio nunca confirmado e já vencido também sai.
        var esquecido = await db.Arquivos.CriarAsync(db.LojaA, 1, new("imagem", "x.png", "image/png", 10, Hash('d')), Ct);
        await db.Execute("SET zippy.sistema = 'sim'; UPDATE social_arquivos SET apagar_em = NOW() - interval '1 minute' WHERE id = @id",
            new { id = esquecido.ArquivoId });

        Assert.Equal(2, await ArquivosLimpezaWorker.Rodada(db.Source, db.Storage, NullLogger.Instance, Ct));
        Assert.Equal(2, db.Storage.Apagados.Count);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_arquivos"));
    }

    [DeliveryDatabaseFact]
    public async Task Dados_do_corte_gravam_e_passando_do_limite_recusam()
    {
        await using var db = await Database.Create();
        var corte = (await db.Editor.CriarExemploAsync(db.LojaA, 1, Ct)).CorteIds[0];
        await db.Editor.SalvarDadosAsync(db.LojaA, corte, Json("""{"falas":[{"quem":"Lucas","t":1.5,"txt":"olha isso"}]}"""), Ct);
        Assert.Contains("Lucas", (await db.Editor.CortesAsync(db.LojaA, new[] { corte }, Ct))[0].Dados.GetRawText());

        var grande = Json($$"""{"x":"{{new string('a', EditorRegras.LimiteDadosBytes)}}"}""");
        Assert.Equal(413, (await Assert.ThrowsAsync<SocialException>(() => db.Editor.SalvarDadosAsync(db.LojaA, corte, grande, Ct))).Status);
        var receitaGrande = Receita(corte, $"\"x\":\"{new string('a', EditorRegras.LimiteReceitaBytes)}\"");
        Assert.Equal(413, (await Assert.ThrowsAsync<SocialException>(() =>
            db.Editor.SalvarReceitaAsync(db.LojaA, 1, corte, new(0, receitaGrande), Ct))).Status);
    }

    /// <summary>R2 de mentira: guarda o tamanho do que "chegou" e o que foi apagado.</summary>
    private sealed class FakeArmazenamento : IArmazenamento
    {
        public Dictionary<string, long> Objetos { get; } = new();
        public List<string> Apagados { get; } = new();
        public bool Configurado => true;
        public List<string> Chaves { get; } = new();
        public string UrlParaEnviar(string key, string mime, long bytes, TimeSpan validade)
        {
            if (!Chaves.Contains(key)) Chaves.Add(key);
            return $"https://r2.teste/enviar/{key}";
        }
        public string UrlParaLer(string key, TimeSpan validade) => $"https://r2.teste/ler/{key}";
        public Task ApagarAsync(string key, CancellationToken ct) { Objetos.Remove(key); Apagados.Add(key); return Task.CompletedTask; }
        public Task<long?> TamanhoAsync(string key, CancellationToken ct) =>
            Task.FromResult(Objetos.TryGetValue(key, out var t) ? t : (long?)null);
        public void Enviar(Guid arquivoId, long bytes) => Objetos[Chaves.First(k => k.Contains(arquivoId.ToString("N")))] = bytes;
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public FakeArmazenamento Storage { get; } = new();
        public SocialEditorService Editor { get; private set; } = null!;
        public SocialArquivosService Arquivos { get; private set; } = null!;
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        public string Schema { get; } = "social_editor_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db.Schema}"); }
            options.SearchPath = db.Schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await db.Execute("""
                CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
                CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
                CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                INSERT INTO estabelecimentos VALUES (@A, 'Loja A'), (@B, 'Loja B');
                INSERT INTO usuario VALUES (1, 'Maria'), (2, 'João');
                """, new { A = db.LojaA, B = db.LojaB });
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261208_01_social_editor_base.sql")));
            db.Editor = new SocialEditorService(db.Source, db.Storage);
            db.Arquivos = new SocialArquivosService(db.Source, db.Storage);
            return db;
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async Task<T?> Scalar<T>(string sql)
        {
            await using var cx = await Source.OpenConnectionAsync();
            await cx.ExecuteAsync("SET zippy.sistema = 'sim'");
            return await cx.ExecuteScalarAsync<T>(sql);
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        }
    }
}
