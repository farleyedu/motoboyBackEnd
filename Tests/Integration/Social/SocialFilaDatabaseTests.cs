using System.Text.Json;
using APIBack.Infrastructure;
using APIBack.Options;
using APIBack.Social.Editor;
using APIBack.Social.Fila;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;
using Xunit;
using Opcoes = Microsoft.Extensions.Options.Options;

namespace APIBack.Tests.Integration.Social;

/// <summary>R14: pareamento, fila e máquinas. Roda só com TEST_DELIVERY_DATABASE (PostgreSQL local, schema próprio descartado).</summary>
public sealed class SocialFilaDatabaseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const int Maria = 1, Joao = 2;

    [DeliveryDatabaseFact]
    public async Task Pareamento_vale_uma_vez_expira_e_o_mesmo_pc_reaproveita_a_maquina()
    {
        await using var db = await Database.Create();
        var codigo = await db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct);
        var pareada = await db.Worker.ParearAsync(Pedido(codigo.Codigo, "pc-da-maria"), Ct);
        var maquina = await db.Worker.AutenticarAsync(pareada.Token, Ct);
        Assert.Equal((pareada.MaquinaId, db.LojaA, Maria), (maquina.Id, maquina.Loja, maquina.Dono));
        Assert.Equal(0, await db.Scalar<int>($"SELECT COUNT(*)::int FROM social_maquinas WHERE token_hash = '{pareada.Token}'"));

        await Codigo("CODIGO_INVALIDO", () => db.Worker.ParearAsync(Pedido(codigo.Codigo, "outro-pc-qualquer"), Ct));

        var vencido = await db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct);
        await db.Sistema("UPDATE social_pareamentos SET expira_em = NOW() - INTERVAL '1 second' WHERE usado_em IS NULL");
        await Codigo("CODIGO_INVALIDO", () => db.Worker.ParearAsync(Pedido(vencido.Codigo, "pc-atrasado"), Ct));

        var outra = await db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct);
        var mesma = await db.Worker.ParearAsync(Pedido(outra.Codigo, "pc-da-maria"), Ct);
        Assert.Equal(pareada.MaquinaId, mesma.MaquinaId);
        await Codigo("MAQUINA_NAO_AUTORIZADA", () => db.Worker.AutenticarAsync(pareada.Token, Ct));
        Assert.Equal(1, (await db.Maquinas.ListarAsync(db.LojaA, Maria, Ct)).Usadas);
    }

    [DeliveryDatabaseFact]
    public async Task Limite_de_maquinas_recusa_codigo_e_pareamento()
    {
        await using var db = await Database.Create(limite: 1);
        var primeiro = await db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct);
        var segundo = await db.Maquinas.NovoCodigoAsync(db.LojaA, Joao, Ct);
        await db.Worker.ParearAsync(Pedido(primeiro.Codigo, "pc-um-da-loja"), Ct);
        await Codigo("LIMITE_MAQUINAS", () => db.Worker.ParearAsync(Pedido(segundo.Codigo, "pc-dois-da-loja"), Ct));
        await Codigo("LIMITE_MAQUINAS", () => db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct));
        // outra loja tem o limite dela
        await db.Maquinas.NovoCodigoAsync(db.LojaB, Joao, Ct);
    }

    [DeliveryDatabaseFact]
    public async Task Gerar_na_maquina_ate_o_fim_sem_duplicar_o_trabalho()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var m = await db.Parear(db.LojaA, Maria, "pc-da-maria");

        var render = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id), Ct);
        var repetido = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id), Ct);
        Assert.Equal(render.Id, repetido.Id);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_trabalhos"));

        var t = (await db.Worker.PegarAsync(m, Ct))!;
        Assert.Equal((render.Id, "renderizar", 1), (t.RenderId, t.Tipo, t.Tentativa));
        Assert.Equal(corte, (long)t.Payload["corteId"]!);
        Assert.Equal(versao, (int)t.Payload["receitaVersao"]!);
        Assert.Equal("gta6", (string?)t.Payload["tema"]);
        Assert.Equal("gameplay.mp4", (string?)t.Payload["original"]!["nome"]);
        Assert.True((bool)t.Payload["original"]!["exemplo"]!);
        Assert.Equal(735m, (decimal)t.Payload["original"]!["ini"]!);
        Assert.True((decimal)t.Payload["dados"]!["dur"]! > 0);
        var arquivo = Assert.Single(t.Payload["arquivos"]!.AsArray());
        Assert.Contains("/ler/", (string?)arquivo!["urlLer"]);
        // um de cada vez
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_maquina_uso"));

        Assert.True((await db.Worker.ProgressoAsync(m, t.Id, new(100, 1200, 1200, 95), Ct)).Continuar);
        Assert.Equal(99m, (await db.Render(db.LojaA, corte, render.Id)).Progresso);

        var fim = new ConcluirRequest(new(1234, 40m, 66m, 1200, 18m, "cpu", "zippy (2).mp4", @"C:\Videos\Zippy\zippy (2).mp4"));
        await db.Worker.ConcluirAsync(m, t.Id, fim, Ct);
        await db.Worker.ConcluirAsync(m, t.Id, new(new(9, 1m, 1m, 1, 1m, "cpu", "outro.mp4", null)), Ct);
        var pronto = await db.Render(db.LojaA, corte, render.Id);
        Assert.Equal(("pronto", 100m, 1234L, "zippy (2).mp4"), (pronto.Status, pronto.Progresso, pronto.Bytes!.Value, pronto.NomeArquivo));
        Assert.False((await db.Worker.ProgressoAsync(m, t.Id, new(50, null, null, null), Ct)).Continuar);
    }

    [DeliveryDatabaseFact]
    public async Task Maquina_de_outra_conta_nao_pega_nem_mexe_no_trabalho()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var minha = await db.Parear(db.LojaA, Maria, "pc-da-maria");
        var deOutraLoja = await db.Parear(db.LojaB, Joao, "pc-do-joao-b");
        var doColega = await db.Parear(db.LojaA, Joao, "pc-do-joao-a");
        var render = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, minha.Id), Ct);

        Assert.Null(await db.Worker.PegarAsync(deOutraLoja, Ct));
        Assert.Null(await db.Worker.PegarAsync(doColega, Ct));
        var t = (await db.Worker.PegarAsync(minha, Ct))!;
        foreach (var intrusa in new[] { deOutraLoja, doColega })
        {
            await Status(404, () => db.Worker.ProgressoAsync(intrusa, t.Id, new(10, null, null, null), Ct));
            await Status(404, () => db.Worker.ConcluirAsync(intrusa, t.Id, new(new(1, 1, 1, 1, 1, "cpu", null, null)), Ct));
            await Status(404, () => db.Worker.FalharAsync(intrusa, t.Id, new("x", false), Ct));
        }
        Assert.Equal("gerando", (await db.Render(db.LojaA, corte, render.Id)).Status);

        // outra loja não usa nem vê a máquina
        await Status(403, () => db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, deOutraLoja.Id), Ct));
        Assert.DoesNotContain((await db.Maquinas.ListarAsync(db.LojaB, Joao, Ct)).Itens, x => x.Id == minha.Id);
        await Status(404, () => db.Maquinas.RemoverAsync(db.LojaB, Joao, minha.Id, Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Compartilhar_libera_o_colega_e_parar_de_compartilhar_tira_o_que_nao_comecou()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var m = await db.Parear(db.LojaA, Maria, "pc-da-maria");

        await Status(403, () => db.Renders.CriarAsync(db.LojaA, Joao, Novo(corte, versao, m.Id), Ct));
        Assert.Empty((await db.Maquinas.ListarAsync(db.LojaA, Joao, Ct)).Itens);
        await Status(404, () => db.Maquinas.MudarAsync(db.LojaA, Joao, m.Id, new(null, true, null), Ct));

        await db.Maquinas.MudarAsync(db.LojaA, Maria, m.Id, new(null, true, null), Ct);
        Assert.False(Assert.Single((await db.Maquinas.ListarAsync(db.LojaA, Joao, Ct)).Itens).Minha);
        await Status(403, () => db.Maquinas.MudarAsync(db.LojaA, Joao, m.Id, new("Minha agora", null, null), Ct));
        var render = await db.Renders.CriarAsync(db.LojaA, Joao, Novo(corte, versao, m.Id), Ct);

        await db.Maquinas.MudarAsync(db.LojaA, Maria, m.Id, new(null, false, null), Ct);
        Assert.Equal("falhou", (await db.Render(db.LojaA, corte, render.Id)).Status);
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Remover_a_maquina_corta_o_token_e_falha_o_que_estava_na_fila()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var codigo = await db.Maquinas.NovoCodigoAsync(db.LojaA, Maria, Ct);
        var pareada = await db.Worker.ParearAsync(Pedido(codigo.Codigo, "pc-da-maria"), Ct);
        var render = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, pareada.MaquinaId), Ct);

        await db.Maquinas.RemoverAsync(db.LojaA, Maria, pareada.MaquinaId, Ct);

        await Codigo("MAQUINA_NAO_AUTORIZADA", () => db.Worker.AutenticarAsync(pareada.Token, Ct));
        var falhou = await db.Render(db.LojaA, corte, render.Id);
        Assert.Equal(("falhou", "A máquina foi removida."), (falhou.Status, falhou.Erro));
        Assert.Empty((await db.Maquinas.ListarAsync(db.LojaA, Maria, Ct)).Itens);
        await Status(403, () => db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, pareada.MaquinaId), Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Falha_e_sumico_repetem_ate_3_vezes()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var m = await db.Parear(db.LojaA, Maria, "pc-da-maria");
        var render = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id), Ct);

        var t1 = (await db.Worker.PegarAsync(m, Ct))!;
        await db.Worker.ProgressoAsync(m, t1.Id, new(40, null, null, null), Ct);
        Assert.True((await db.Worker.FalharAsync(m, t1.Id, new("Fechei o programa.", true), Ct)).VaiRepetir);
        Assert.Equal(("gerando", 0m), await db.Situacao(db.LojaA, corte, render.Id));

        // a máquina some no meio da segunda: depois de 2 min o trabalho volta
        var t2 = (await db.Worker.PegarAsync(m, Ct))!;
        Assert.Equal(2, t2.Tentativa);
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
        await db.Sistema("UPDATE social_trabalhos SET sinal_em = NOW() - INTERVAL '3 minutes'");
        var t3 = (await db.Worker.PegarAsync(m, Ct))!;
        Assert.Equal(3, t3.Tentativa);

        Assert.False((await db.Worker.FalharAsync(m, t3.Id, new("FFmpeg morreu.", true), Ct)).VaiRepetir);
        var fim = await db.Render(db.LojaA, corte, render.Id);
        Assert.Equal(("falhou", "FFmpeg morreu."), (fim.Status, fim.Erro));
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Cancelar_no_painel_chega_na_maquina_e_a_receita_mudada_e_recusada()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var m = await db.Parear(db.LojaA, Maria, "pc-da-maria");
        await Codigo("RECEITA_MUDOU", () => db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao + 1, m.Id), Ct));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_renders"));

        var render = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id), Ct);
        var t = (await db.Worker.PegarAsync(m, Ct))!;
        Assert.Empty((await db.Worker.SinalAsync(m, Sinal(t.Id), Ct)).Cancelar);
        await Codigo("RENDER_DE_MAQUINA", () => db.Renders.AtualizarAsync(db.LojaA, render.Id, new("pronto", null, 5, null, null, null, null), Ct));
        await db.Renders.AtualizarAsync(db.LojaA, render.Id, new("cancelado", null, null, null, null, null, null), Ct);

        Assert.Equal(new[] { t.Id }, (await db.Worker.SinalAsync(m, Sinal(t.Id), Ct)).Cancelar);
        Assert.False((await db.Worker.ProgressoAsync(m, t.Id, new(60, null, null, null), Ct)).Continuar);
        await db.Worker.ConcluirAsync(m, t.Id, new(new(5, 1, 1, 1, 1, "cpu", null, null)), Ct);
        Assert.Equal("cancelado", (await db.Render(db.LojaA, corte, render.Id)).Status);

        // outra qualidade depois: render e trabalho novos
        var outro = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id, "720p"), Ct);
        var maior = await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id, "4K"), Ct);
        Assert.Equal("cancelado", (await db.Render(db.LojaA, corte, outro.Id)).Status);
        Assert.Equal(maior.Id, (await db.Worker.PegarAsync(m, Ct))!.RenderId);
    }

    [DeliveryDatabaseFact]
    public async Task Pausada_desativada_ou_loja_bloqueada_nao_recebe_trabalho()
    {
        await using var db = await Database.Create();
        var (corte, versao) = await db.CorteComReceita(db.LojaA);
        var m = await db.Parear(db.LojaA, Maria, "pc-da-maria");
        await db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id), Ct);

        db.Loja.Livre = false;
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
        Assert.True((await db.Worker.SinalAsync(m, Sinal(null), Ct)).Pausada);
        db.Loja.Livre = true;

        await db.Maquinas.MudarAsync(db.LojaA, Maria, m.Id, new(null, null, false), Ct);
        Assert.Null(await db.Worker.PegarAsync(m, Ct));
        await Codigo("MAQUINA_DESLIGADA", () => db.Renders.CriarAsync(db.LojaA, Maria, Novo(corte, versao, m.Id, "720p"), Ct));
        await db.Maquinas.MudarAsync(db.LojaA, Maria, m.Id, new(null, null, true), Ct);
        Assert.NotNull(await db.Worker.PegarAsync(m, Ct));
    }

    [DeliveryDatabaseFact]
    public async Task Rollback_recusa_com_maquina_pareada_e_a_migration_roda_de_novo()
    {
        await using var db = await Database.Create();
        await db.Parear(db.LojaA, Maria, "pc-da-maria");
        await db.Rodar("20261208_05_social_fila_maquinas.sql");
        var erro = await Assert.ThrowsAsync<PostgresException>(() => db.Rodar("rollback/20261208_05_social_fila_maquinas.down.sql"));
        Assert.Contains("Rollback bloqueado", erro.MessageText);
        await db.Sistema("UPDATE social_maquinas SET removida_em = NOW(), token_hash = NULL");
        await db.Rodar("rollback/20261208_05_social_fila_maquinas.down.sql");
        await db.Rodar("20261208_05_social_fila_maquinas.sql");
    }

    private static ParearRequest Pedido(string codigo, string dispositivo) =>
        new(codigo, dispositivo, "PC-Casa", "1.0.0", false, null, 50_000_000_000);

    private static SinalRequest Sinal(Guid? trabalho) => new("1.0.0", false, null, 1, trabalho);

    private static NovoRenderRequest Novo(long corte, int versao, long maquina, string qualidade = "1080p") =>
        new(corte, versao, "maquina", maquina, "shorts", qualidade, 40m, "zippy.mp4", "gta6",
            new OriginalDto(999, false, "gameplay.mp4", 5000, 999m));

    private static async Task Codigo(string codigo, Func<Task> f) => Assert.Equal(codigo, (await Assert.ThrowsAsync<SocialException>(f)).Codigo);
    private static async Task Status(int status, Func<Task> f) => Assert.Equal(status, (await Assert.ThrowsAsync<SocialException>(f)).Status);

    private sealed class Liberacao : ISocialLojaLiberada
    {
        public bool Livre { get; set; } = true;
        public Task<bool> Liberada(Guid loja, CancellationToken ct) => Task.FromResult(Livre);
    }

    private sealed class FakeArmazenamento : APIBack.Social.Arquivos.IArmazenamento
    {
        public bool Configurado => true;
        public string UrlParaEnviar(string key, string mime, long bytes, TimeSpan validade) => $"https://r2.teste/enviar/{key}";
        public string UrlParaLer(string key, TimeSpan validade) => $"https://r2.teste/ler/{key}";
        public Task ApagarAsync(string key, CancellationToken ct) => Task.CompletedTask;
        public Task<long?> TamanhoAsync(string key, CancellationToken ct) => Task.FromResult<long?>(null);
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public FakeArmazenamento Storage { get; } = new();
        public Liberacao Loja { get; } = new();
        public SocialEditorService Editor { get; private set; } = null!;
        public SocialRendersService Renders { get; private set; } = null!;
        public SocialMaquinasService Maquinas { get; private set; } = null!;
        public SocialWorkerService Worker { get; private set; } = null!;
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        public string Schema { get; } = "social_fila_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Database> Create(int limite = 3)
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db.Schema}"); }
            options.SearchPath = db.Schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await using (var cx = await db.Source.OpenConnectionAsync())
                await cx.ExecuteAsync("""
                    CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
                    CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
                    CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                    INSERT INTO estabelecimentos VALUES (@A, 'Loja A'), (@B, 'Loja B');
                    INSERT INTO usuario VALUES (1, 'Maria'), (2, 'João');
                    """, new { A = db.LojaA, B = db.LojaB });
            await db.Rodar("20261208_01_social_editor_base.sql");
            await db.Rodar("20261208_04_social_renders.sql");
            await db.Rodar("20261208_05_social_fila_maquinas.sql");
            var opcoes = Opcoes.Create(new SocialMaquinasOptions { LimitePorLoja = limite });
            db.Editor = new SocialEditorService(db.Source, db.Storage);
            db.Renders = new SocialRendersService(db.Source, db.Storage);
            db.Maquinas = new SocialMaquinasService(db.Source, opcoes);
            db.Worker = new SocialWorkerService(db.Source, db.Storage, opcoes, db.Loja);
            return db;
        }

        public async Task Rodar(string arquivo)
        {
            // conexão própria: um script que falha no meio deixa a transação aberta
            await using var cx = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_base) { SearchPath = Schema, Pooling = false }.ConnectionString);
            await cx.OpenAsync();
            await cx.ExecuteAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", arquivo)));
        }

        public async Task<Maquina> Parear(Guid loja, int usuario, string dispositivo)
        {
            var codigo = await Maquinas.NovoCodigoAsync(loja, usuario, Ct);
            var pareada = await Worker.ParearAsync(Pedido(codigo.Codigo, dispositivo), Ct);
            return await Worker.AutenticarAsync(pareada.Token, Ct);
        }

        /// <summary>Um corte de exemplo com receita salva que cita um arquivo já enviado.</summary>
        public async Task<(long Corte, int Versao)> CorteComReceita(Guid loja)
        {
            var corte = (await Editor.CriarExemploAsync(loja, 1, Ct)).CorteIds[0];
            var arquivo = Guid.NewGuid();
            await Sistema($"""
                INSERT INTO social_arquivos (id, estabelecimento_id, dono_usuario_id, tipo, nome, mime, bytes, r2_key, hash_sha256, enviado_em)
                VALUES ('{arquivo}', '{loja}', 1, 'imagem', 'logo.png', 'image/png', 10, 'social/{loja:D}/arquivos/a.png', repeat('a', 64), NOW())
                """);
            var receita = JsonDocument.Parse($$$"""{"v":1,"corteId":{{{corte}}},"clips":[],"wm":{"arquivoId":"{{{arquivo}}}"}}""").RootElement.Clone();
            return (corte, (await Editor.SalvarReceitaAsync(loja, 1, corte, new(0, receita), Ct)).Versao);
        }

        public async Task<RenderDto> Render(Guid loja, long corte, Guid id) =>
            (await Renders.ListarAsync(loja, new[] { corte }, Ct)).Single(r => r.Id == id);

        public async Task<(string, decimal)> Situacao(Guid loja, long corte, Guid id)
        {
            var r = await Render(loja, corte, id);
            return (r.Status, r.Progresso);
        }

        public async Task Sistema(string sql) => await Scalar<int>(sql);

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
