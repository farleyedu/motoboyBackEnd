using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Planta;
using APIBack.Tests.Unit.Mesas.Planta;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Mesas.Planta;

/// <summary>Rascunho, publicar e cadastro de mesas (P05), num schema isolado do PostgreSQL local.</summary>
public sealed class PlantaDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Publicar_com_erro_nao_troca_a_publicada()
    {
        await using var db = await Database.Create();
        var v1 = await db.Publicar(PlantaRegrasTests.Planta());
        Assert.Equal(1, v1.VersaoPlanta);
        Assert.Equal(3, v1.MesasCriadas);

        var ruim = PlantaRegrasTests.Planta(d => { ((JsonObject)d["mesas"]![1]!)["numero"] = 1; ((JsonObject)d["pisos"]![0]!)["elementos"] = new JsonArray(); });
        var salvo = await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, ruim, null), default);
        var erro = await Assert.ThrowsAsync<MesasException>(() => db.Service.PublicarAsync(db.Gerente, new(null, salvo.VersaoRascunho), default));
        Assert.Equal("VALIDACAO", erro.Codigo);
        var erros = (IReadOnlyList<PlantaErro>)erro.Detalhes!.GetType().GetProperty("erros")!.GetValue(erro.Detalhes)!;
        Assert.Contains(erros, e => e.Codigo == "SEM_ENTRADA");
        Assert.Contains(erros, e => e.Codigo == "NUMERO_REPETIDO");

        var publicada = await db.Service.LerAsync(db.A, "publicada", null, default);
        Assert.Equal(1, publicada!.Versao);
        Assert.NotNull(await db.Service.LerAsync(db.A, "rascunho", null, default)); // o rascunho fica para corrigir
        Assert.Equal(new[] { "1", "2", "3" }, await db.Numeros());
        Assert.Equal(0, await db.Escalar<int>("SELECT count(*) FROM mesa_evento WHERE acao = 'publicarPlanta' AND depois = 'Versão 2'"));
        Assert.Empty(db.TempoReal.Salao.Where(v => v == 2));
    }

    [DeliveryDatabaseFact]
    public async Task Mesa_renumerada_mantem_id_e_historico()
    {
        await using var db = await Database.Create();
        await db.Publicar(PlantaRegrasTests.Planta());
        var antes = await db.Escalar<Guid>("SELECT id FROM mesa WHERE chave_planta = 'm-peq-3'");

        // Troca 1 e 2 entre si e renumera a 3 para 13 na mesma publicação.
        var v2 = await db.Publicar(PlantaRegrasTests.Planta(d =>
        {
            ((JsonObject)d["mesas"]![0]!)["numero"] = 2;
            ((JsonObject)d["mesas"]![1]!)["numero"] = 1;
            ((JsonObject)d["mesas"]![2]!)["numero"] = 13;
        }));
        Assert.Equal(2, v2.VersaoPlanta);
        Assert.Equal(0, v2.MesasCriadas);
        Assert.Equal(3, v2.MesasRenumeradas);

        Assert.Equal(antes, await db.Escalar<Guid>("SELECT id FROM mesa WHERE numero = '13' AND ativa"));
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_evento WHERE acao = 'renumerarMesa' AND mesa_id = @antes AND antes = '3' AND depois = '13'", new { antes }));
        Assert.Equal(3, await db.Escalar<int>("SELECT count(*) FROM mesa"));
        Assert.Equal("arquivada", await db.Escalar<string>("SELECT estado FROM mesa_planta WHERE versao = 1"));
        Assert.Equal(new[] { 1, 2 }, db.TempoReal.Salao);
    }

    [DeliveryDatabaseFact]
    public async Task Rascunho_concorrente_devolve_conflito()
    {
        await using var db = await Database.Create();
        var primeiro = await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(), null), default);
        Assert.Equal(1, primeiro.VersaoRascunho);

        // Ana e o dono abriram a revisão 1; Ana salva primeiro, o dono recebe conflito e o rascunho dela fica.
        await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(d => d["autor"] = "ana"), 1), default);
        var conflito = await Assert.ThrowsAsync<MesasException>(() =>
            db.Service.SalvarRascunhoAsync(db.Dono, new(null, PlantaRegrasTests.Planta(d => d["autor"] = "dono"), 1), default));
        Assert.Equal("VERSAO_VELHA", conflito.Codigo);
        Assert.Equal(409, conflito.Status);
        Assert.Contains("Ana", conflito.Message);
        Assert.Equal("ana", (await db.Service.LerAsync(db.A, "rascunho", null, default))!.Documento["autor"]!.GetValue<string>());

        // Salvar sem versão quando já há rascunho também é conflito; publicar com versão velha idem.
        var semVersao = await Assert.ThrowsAsync<MesasException>(() => db.Service.SalvarRascunhoAsync(db.Dono, new(null, PlantaRegrasTests.Planta(), null), default));
        Assert.Equal("VERSAO_VELHA", semVersao.Codigo);
        var publicarVelho = await Assert.ThrowsAsync<MesasException>(() => db.Service.PublicarAsync(db.Dono, new(null, 1), default));
        Assert.Equal("VERSAO_VELHA", publicarVelho.Codigo);

        // Corrida de verdade: duas gravações da mesma revisão ao mesmo tempo, só uma passa.
        var r = await Task.WhenAll(Tentar(db, 2), Tentar(db, 2));
        Assert.Equal(1, r.Count(ok => ok));
    }

    [DeliveryDatabaseFact]
    public async Task Mesa_em_uso_nao_sai_da_planta_e_bloqueio_respeita_estado()
    {
        await using var db = await Database.Create();
        await db.Publicar(PlantaRegrasTests.Planta());
        var mesa2 = await db.Escalar<Guid>("SELECT id FROM mesa WHERE numero = '2'");
        await db.Execute("UPDATE mesa_estado SET status = 'ocupada', sessao_id = gen_random_uuid() WHERE mesa_id = @mesa2", new { mesa2 });

        var semMesa2 = PlantaRegrasTests.Planta(d => ((JsonArray)d["mesas"]!).RemoveAt(1));
        var salvo = await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, semMesa2, null), default);
        var erro = await Assert.ThrowsAsync<MesasException>(() => db.Service.PublicarAsync(db.Gerente, new(null, salvo.VersaoRascunho), default));
        Assert.Equal("ESTADO_INVALIDO", erro.Codigo);
        Assert.True(await db.Escalar<bool>("SELECT ativa FROM mesa WHERE id = @mesa2", new { mesa2 }));

        var aberta = await Assert.ThrowsAsync<MesasException>(() => db.Service.BloquearAsync(db.Gerente, new(null, mesa2, 1, "Cadeira quebrada"), true, default));
        Assert.Equal("ESTADO_INVALIDO", aberta.Codigo);

        var mesa1 = await db.Escalar<Guid>("SELECT id FROM mesa WHERE numero = '1'");
        var semMotivo = await Assert.ThrowsAsync<MesasException>(() => db.Service.BloquearAsync(db.Gerente, new(null, mesa1, 1, null), true, default));
        Assert.Equal("EXIGE_MOTIVO", semMotivo.Codigo);
        var bloqueio = await db.Service.BloquearAsync(db.Gerente, new(null, mesa1, 1, "Cadeira quebrada"), true, default);
        Assert.Equal(2, bloqueio.Versoes[mesa1]);
        var velho = await Assert.ThrowsAsync<MesasException>(() => db.Service.BloquearAsync(db.Gerente, new(null, mesa1, 1, "x"), false, default));
        Assert.Equal("VERSAO_VELHA", velho.Codigo);
        await db.Service.BloquearAsync(db.Gerente, new(null, mesa1, 2, null), false, default);

        var app = await db.Service.AppAsync(db.A, default);
        Assert.Equal(3, app!.Mesas.Count);
        Assert.Null(((JsonObject)app.Pisos[0]!)["fundo"]);
        Assert.NotNull(app.Mesas[0].Desenho);
    }

    [DeliveryDatabaseFact]
    public async Task Publicar_sem_rascunho_e_ja_feito_e_descartar_guarda()
    {
        await using var db = await Database.Create();
        var nada = await Assert.ThrowsAsync<MesasException>(() => db.Service.PublicarAsync(db.Gerente, new(null, 1), default));
        Assert.Equal("VALIDACAO", nada.Codigo);
        await db.Publicar(PlantaRegrasTests.Planta());
        var ja = await Assert.ThrowsAsync<MesasException>(() => db.Service.PublicarAsync(db.Gerente, new(null, 1), default));
        Assert.Equal("JA_FEITO", ja.Codigo);

        var salvo = await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(), null), default);
        Assert.Equal(2, salvo.VersaoPlanta);
        await db.Service.DescartarAsync(db.Gerente, new(null, salvo.VersaoRascunho), default);
        Assert.Null(await db.Service.LerAsync(db.A, "rascunho", null, default));
        var versoes = await db.Service.VersoesAsync(db.A, default);
        Assert.Equal(new[] { "descartada", "publicada" }, versoes.Select(v => v.Estado));
        Assert.Empty(await db.Service.VersoesAsync(db.B, default));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("DELETE FROM mesa"));
    }

    private static async Task<bool> Tentar(Database db, int revisao)
    {
        try { await db.Service.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(), revisao), default); return true; }
        catch (MesasException ex) when (ex.Codigo == "VERSAO_VELHA") { return false; }
    }

    private sealed class TempoRealFalso : IPlantaTempoReal
    {
        public List<int> Salao { get; } = new();
        public Task SalaoMudou(Guid loja, int versaoPlanta) { lock (Salao) Salao.Add(versaoPlanta); return Task.CompletedTask; }
        public Task MesaMudou(Guid loja, Guid mesaId, int versao) => Task.CompletedTask;
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public PlantaService Service { get; private set; } = null!;
        public TempoRealFalso TempoReal { get; } = new();
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public MesasContexto Gerente => new(A, 10, MesasPermissoes.PapelGerente);
        public MesasContexto Dono => new(A, 20, MesasPermissoes.PapelDono);
        private readonly string _schema = "mesas_planta_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            db.Service = new PlantaService(db.Source, db.TempoReal);
            await db.Execute("""
                CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text, modulos_ativos text[]);
                CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
                CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                CREATE TABLE gestao_pessoas (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos (id),
                  nome varchar(120) NOT NULL, funcao varchar(60) NOT NULL DEFAULT '', vinculo varchar(12) NOT NULL,
                  usuario_id integer REFERENCES usuario (id), ativo boolean NOT NULL DEFAULT true,
                  atualizado_por integer, atualizado_em timestamptz NOT NULL DEFAULT now());
                CREATE FUNCTION gestao_bloqueia_alteracao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'imutavel'; END $$;
                CREATE FUNCTION gestao_bloqueia_exclusao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'sem exclusao'; END $$;
                INSERT INTO estabelecimentos VALUES (@A, 'Loja A', ARRAY['MESAS']), (@B, 'Loja B', ARRAY['MESAS']);
                INSERT INTO usuario VALUES (10, 'Ana'), (20, 'Dono');
                """, new { db.A, db.B });
            foreach (var arquivo in new[] { "20261203_03_mesas_nucleo.sql", "20261205_05_mesas_planta.sql", "20261205_05_mesas_planta.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", arquivo)));
            return db;
        }

        public async Task<PlantaPublicadaDto> Publicar(JsonObject doc)
        {
            var atual = await Service.LerAsync(A, "rascunho", null, default);
            var salvo = await Service.SalvarRascunhoAsync(Gerente, new(null, doc, atual?.VersaoRascunho), default);
            return await Service.PublicarAsync(Gerente, new(null, salvo.VersaoRascunho), default);
        }

        public async Task<string[]> Numeros()
        {
            await using var cx = await Source.OpenConnectionAsync();
            return (await cx.QueryAsync<string>("SELECT numero FROM mesa WHERE ativa ORDER BY numero")).ToArray();
        }

        public async Task<T> Escalar<T>(string sql, object? args = null)
        {
            await using var cx = await Source.OpenConnectionAsync();
            return await cx.ExecuteScalarAsync<T>(sql, args) ?? throw new InvalidOperationException("Sem resultado: " + sql);
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
