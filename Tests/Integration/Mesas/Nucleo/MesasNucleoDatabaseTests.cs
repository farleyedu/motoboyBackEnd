using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Mesas.Nucleo;

/// <summary>Configuração com histórico e funções da conta (P01), num schema isolado do PostgreSQL local.</summary>
public sealed class MesasNucleoDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Me_funcoes_devolve_uma_e_duas_funcoes_por_loja()
    {
        await using var db = await Database.Create();
        var service = new MesasNucleoService(db.Source);
        await db.Execute("""
            INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, vinculo, usuario_id, funcao_codigo) VALUES
              (gen_random_uuid(), @A, 'Ana', 'fixo', 10, 'garcom'),
              (gen_random_uuid(), @B, 'Bruno', 'fixo', 20, 'garcom'),
              (gen_random_uuid(), @B, 'Sem função', 'fixo', 20, NULL);
            INSERT INTO motoboy VALUES (5, 20, 5);
            INSERT INTO motoboy_estabelecimento VALUES (5, @B, TRUE);
            """, new { db.A, db.B });

        var ana = await service.MinhasFuncoesAsync(10, default);
        var loja = Assert.Single(ana);
        Assert.Equal(db.A, loja.EstabelecimentoId);
        Assert.Equal(new[] { "garcom" }, loja.Funcoes);
        Assert.True(loja.MesasContratado);
        Assert.False(loja.MesasLigado);

        var bruno = Assert.Single(await service.MinhasFuncoesAsync(20, default));
        Assert.Equal(new[] { "garcom", "motoboy" }, bruno.Funcoes.OrderBy(f => f));
        Assert.NotNull(bruno.PessoaId);

        Assert.Empty(await service.MinhasFuncoesAsync(99, default));
    }

    [DeliveryDatabaseFact]
    public async Task Configuracao_grava_historico_e_repetir_nao_duplica()
    {
        await using var db = await Database.Create();
        var service = new MesasNucleoService(db.Source);

        var inicial = await service.ConfigAsync(db.A, default);
        Assert.False(inicial.Ativo);
        Assert.Equal(0, inicial.Versao);
        Assert.Equal("[5,10,15]", MesasConfigRegras.Ler(inicial.Dados, "gorjeta.sugestoes")!.ToJsonString());

        var req = new MesasConfigurarRequest("gorjeta.sugestoes", new JsonArray(10, 12), "Sugestões de gorjeta");
        var depois = await service.ConfigurarAsync(db.A, 10, MesasPermissoes.PapelGerente, req, default);
        Assert.Equal(2, depois.Versao);
        Assert.Equal("[10,12]", MesasConfigRegras.Ler(depois.Dados, "gorjeta.sugestoes")!.ToJsonString());
        Assert.True(MesasConfigRegras.Ler(depois.Dados, "gorjeta.ativa")!.GetValue<bool>());

        var repetido = await service.ConfigurarAsync(db.A, 10, MesasPermissoes.PapelGerente, req, default);
        Assert.Equal(2, repetido.Versao);

        var historico = Assert.Single(await service.HistoricoAsync(db.A, 50, default));
        Assert.Equal("gorjeta.sugestoes", historico.Chave);
        Assert.Equal("[5, 10, 15]", historico.Antes);
        Assert.Equal("Sugestões de gorjeta", historico.Rotulo);
        Assert.Equal("Ana", historico.PorNome);

        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE mesas_config_historico SET rotulo = 'x'"));
        Assert.Empty(await service.HistoricoAsync(db.B, 50, default));
    }

    [DeliveryDatabaseFact]
    public async Task Ligar_e_so_do_dono_e_valor_invalido_e_recusado()
    {
        await using var db = await Database.Create();
        var service = new MesasNucleoService(db.Source);
        var ligar = new MesasConfigurarRequest("ativo", JsonValue.Create(true), "Ligou o Mesas");

        var negado = await Assert.ThrowsAsync<MesasException>(() => service.ConfigurarAsync(db.A, 10, MesasPermissoes.PapelGerente, ligar, default));
        Assert.Equal("SEM_PERMISSAO", negado.Codigo);
        Assert.False(await service.LigadoAsync(db.A, default));

        var config = await service.ConfigurarAsync(db.A, 10, MesasPermissoes.PapelDono, ligar, default);
        Assert.True(config.Ativo);
        Assert.True(await service.LigadoAsync(db.A, default));

        var invalido = await Assert.ThrowsAsync<MesasException>(() => service.ConfigurarAsync(db.A, 10, MesasPermissoes.PapelDono,
            new MesasConfigurarRequest("aprovacao.modo", JsonValue.Create("qualquer"), null), default));
        Assert.Equal("VALIDACAO", invalido.Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task Funcao_da_pessoa_e_gravada_so_na_loja_dela()
    {
        await using var db = await Database.Create();
        var service = new MesasNucleoService(db.Source);
        var pessoa = Guid.NewGuid();
        await db.Execute("INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, vinculo, usuario_id) VALUES (@pessoa, @A, 'Ana', 'fixo', 10)",
            new { pessoa, db.A });

        var gravada = await service.DefinirFuncaoAsync(db.A, 10, pessoa, "cozinha", default);
        Assert.Equal("cozinha", gravada.FuncaoCodigo);
        Assert.Equal("cozinha", await service.FuncaoDoUsuarioAsync(db.A, 10, default));

        await Assert.ThrowsAsync<MesasException>(() => service.DefinirFuncaoAsync(db.B, 10, pessoa, "garcom", default));
        await Assert.ThrowsAsync<MesasException>(() => service.DefinirFuncaoAsync(db.A, 10, pessoa, "motoboy", default));
        Assert.Null((await service.DefinirFuncaoAsync(db.A, 10, pessoa, null, default)).FuncaoCodigo);
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        private readonly string _schema = "mesas_nucleo_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            // Só o que a migração e o serviço tocam; gestao_pessoas no formato de 20261109_01 (colunas usadas aqui).
            await db.Execute("""
                CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text, modulos_ativos text[]);
                CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
                CREATE TABLE motoboy (id int PRIMARY KEY, id_usuario int, canonical_motoboy_id int);
                CREATE TABLE motoboy_estabelecimento (motoboy_id int, estabelecimento_id uuid, ativo bool);
                CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                CREATE TABLE gestao_pessoas (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos (id),
                  nome varchar(120) NOT NULL, funcao varchar(60) NOT NULL DEFAULT '', vinculo varchar(12) NOT NULL,
                  usuario_id integer REFERENCES usuario (id), ativo boolean NOT NULL DEFAULT true,
                  atualizado_por integer, atualizado_em timestamptz NOT NULL DEFAULT now());
                CREATE FUNCTION gestao_bloqueia_alteracao() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'imutavel'; END $$;
                INSERT INTO estabelecimentos VALUES (@A, 'Loja A', ARRAY['PEDIDOS','MESAS']), (@B, 'Loja B', ARRAY['PEDIDOS']);
                INSERT INTO usuario VALUES (10, 'Ana'), (20, 'Bruno');
                """, new { db.A, db.B });
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261203_03_mesas_nucleo.sql")));
            // Idempotente: rodar de novo não quebra.
            await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261203_03_mesas_nucleo.sql")));
            return db;
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
