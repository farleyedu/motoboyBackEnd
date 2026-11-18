using System.Collections.Concurrent;
using APIBack.Equipe.Ponto;
using APIBack.Tests.Integration;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Equipe.Ponto;

/// <summary>Ponto da equipe (P02) num schema isolado do PostgreSQL local, com a Equipe simulada (fuso e preenchimento do turno).</summary>
public sealed class PontoDatabaseTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo");
    private static readonly TimeZoneInfo Manaus = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Brazilian Standard Time" : "America/Manaus");

    // 17/11/2026 18:00 em São Paulo.
    private static readonly DateTimeOffset Inicio = new(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);

    [DeliveryDatabaseFact]
    public async Task Ponto_duplicado_responde_ja_feito_e_fica_um_so()
    {
        await using var db = await Database.Create();
        var (svc, _, _, _) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);

        var entrou = await svc.Entrar(ana, new PontoEntrarRequest(), default);
        Assert.Equal("garcom", entrou.Ponto.Modo);
        Assert.Equal("18:00", entrou.Ponto.EntradaHora);

        var ex = await Assert.ThrowsAsync<PontoException>(() => svc.Entrar(ana, new PontoEntrarRequest(), default));
        Assert.Equal("JA_FEITO", ex.Codigo);
        Assert.Contains("18:00", ex.Message);
        Assert.Equal(1, await db.Scalar<long>("SELECT count(*) FROM gestao_ponto"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("DELETE FROM gestao_ponto"));
    }

    [DeliveryDatabaseFact]
    public async Task Entradas_simultaneas_abrem_um_ponto()
    {
        await using var db = await Database.Create();
        var (svc, _, _, _) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await svc.Entrar(ana, new PontoEntrarRequest(), default); return "ok"; }
            catch (PontoException e) { return e.Codigo; }
        }));
        Assert.Equal(1, resultados.Count(r => r == "ok"));
        Assert.All(resultados.Where(r => r != "ok"), r => Assert.Equal("JA_FEITO", r));
    }

    [DeliveryDatabaseFact]
    public async Task Pausa_sem_entrada_e_estados_proibidos()
    {
        await using var db = await Database.Create();
        var (svc, relogio, _, _) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);

        Assert.Equal("ESTADO_INVALIDO", (await Assert.ThrowsAsync<PontoException>(() => svc.Pausar(ana, new PontoPausarRequest(), default))).Codigo);
        Assert.Equal("ESTADO_INVALIDO", (await Assert.ThrowsAsync<PontoException>(() => svc.Voltar(ana, new PontoVoltarRequest(), default))).Codigo);
        Assert.Equal("ESTADO_INVALIDO", (await Assert.ThrowsAsync<PontoException>(() => svc.Sair(ana, new PontoSairRequest(), default))).Codigo);

        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        Assert.Equal("ESTADO_INVALIDO", (await Assert.ThrowsAsync<PontoException>(() => svc.Voltar(ana, new PontoVoltarRequest(), default))).Codigo);
        relogio.Avancar(TimeSpan.FromMinutes(5));
        await svc.Pausar(ana, new PontoPausarRequest(null, "Almoço"), default);
        var dupla = await Assert.ThrowsAsync<PontoException>(() => svc.Pausar(ana, new PontoPausarRequest(), default));
        Assert.Equal("ESTADO_INVALIDO", dupla.Codigo);
        Assert.Contains("18:05", dupla.Message);
    }

    [DeliveryDatabaseFact]
    public async Task Jornada_completa_preenche_o_turno_realizado_com_pausas_no_dia()
    {
        await using var db = await Database.Create();
        var (svc, relogio, equipe, avisos) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);

        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        relogio.Avancar(TimeSpan.FromMinutes(120));
        await svc.Pausar(ana, new PontoPausarRequest(null, "Jantar"), default);
        relogio.Avancar(TimeSpan.FromMinutes(30));
        var meio = await svc.Dia(ana, null, null, default);
        Assert.Equal("pausa", meio.Estado);
        await svc.Voltar(ana, new PontoVoltarRequest(), default);
        relogio.Avancar(TimeSpan.FromMinutes(180));
        var saiu = await svc.Sair(ana, new PontoSairRequest(), default);

        Assert.Equal(300, saiu.Resumo.Minutos);
        Assert.Equal("23:30", saiu.Ponto.SaidaHora);
        Assert.Equal("pessoa", saiu.Ponto.FechadoPor);
        Assert.Equal("preenchido", saiu.Ponto.TurnoSituacao);
        var chamada = Assert.Single(equipe.Preenchidos);
        Assert.Equal((db.Ana, new DateOnly(2026, 11, 17), "18:00", "23:30"), (chamada.Pessoa, chamada.Dia, chamada.Entrada, chamada.Saida));
        Assert.Equal("realizado", await db.Scalar<string>("SELECT status FROM gestao_turnos WHERE pessoa_id = @p", new { p = db.Ana }));

        var dia = await svc.Dia(ana, null, new DateOnly(2026, 11, 17), default);
        Assert.Equal("encerrado", dia.Estado);
        var ponto = Assert.Single(dia.Pontos);
        var pausa = Assert.Single(ponto.Pausas);
        Assert.Equal(("Jantar", 30), (pausa.Motivo, pausa.Minutos));
        Assert.Equal(30, dia.MinutosPausa);
        Assert.Equal(4, avisos.Count(a => a == db.Ana)); // entrar, pausar, voltar, sair
    }

    [DeliveryDatabaseFact]
    public async Task Virada_de_dia_fica_no_turno_do_dia_da_entrada()
    {
        await using var db = await Database.Create();
        // 17/11 22:50 em São Paulo (18/11 01:50 UTC).
        var (svc, relogio, equipe, _) = db.Servico(new DateTimeOffset(2026, 11, 18, 1, 50, 0, TimeSpan.Zero));
        var ana = db.Ator(db.Ana);

        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        relogio.Avancar(TimeSpan.FromMinutes(200)); // 18/11 02:10 local
        var hoje = await svc.Dia(ana, null, null, default);
        Assert.Equal(new DateOnly(2026, 11, 18), hoje.Dia);
        Assert.Equal("ativo", hoje.Estado);
        Assert.Equal(new DateOnly(2026, 11, 17), hoje.Aberto!.Dia);

        var saiu = await svc.Sair(ana, new PontoSairRequest(), default);
        Assert.Equal(new DateOnly(2026, 11, 17), saiu.Ponto.Dia);
        var chamada = Assert.Single(equipe.Preenchidos);
        Assert.Equal((new DateOnly(2026, 11, 17), "22:50", "02:10"), (chamada.Dia, chamada.Entrada, chamada.Saida));
    }

    [DeliveryDatabaseFact]
    public async Task Fuso_da_loja_define_a_hora_gravada_no_turno()
    {
        await using var db = await Database.Create();
        var (svc, relogio, equipe, _) = db.Servico(Inicio, Manaus);
        var ana = db.Ator(db.Ana);

        var entrou = await svc.Entrar(ana, new PontoEntrarRequest(), default);
        Assert.Equal("17:00", entrou.Ponto.EntradaHora);
        relogio.Avancar(TimeSpan.FromMinutes(61));
        await svc.Sair(ana, new PontoSairRequest(), default);
        Assert.Equal(("17:00", "18:01"), (equipe.Preenchidos[0].Entrada, equipe.Preenchidos[0].Saida));
    }

    [DeliveryDatabaseFact]
    public async Task Turno_que_o_gerente_ja_confirmou_nao_e_sobrescrito()
    {
        await using var db = await Database.Create();
        await db.Execute("""
            INSERT INTO gestao_turnos (id, estabelecimento_id, pessoa_id, data, entrada, saida, status, acontecimento_id)
            VALUES (gen_random_uuid(), @A, @p, '2026-11-17', '18:00', '22:00', 'realizado', gen_random_uuid())
            """, new { db.A, p = db.Ana });
        var (svc, relogio, equipe, _) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);

        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        relogio.Avancar(TimeSpan.FromMinutes(300));
        var saiu = await svc.Sair(ana, new PontoSairRequest(), default);
        Assert.Equal("mantido", saiu.Ponto.TurnoSituacao);
        Assert.Empty(equipe.Preenchidos);
        Assert.Equal("22:00", await db.Scalar<string>("SELECT saida FROM gestao_turnos WHERE pessoa_id = @p", new { p = db.Ana }));
    }

    [DeliveryDatabaseFact]
    public async Task Segundo_ponto_do_dia_estende_o_turno_que_o_ponto_preencheu()
    {
        await using var db = await Database.Create();
        var (svc, relogio, equipe, _) = db.Servico(new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero)); // 11:00
        var ana = db.Ator(db.Ana);

        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        relogio.Avancar(TimeSpan.FromHours(4));
        await svc.Sair(ana, new PontoSairRequest(), default);
        relogio.Avancar(TimeSpan.FromHours(3));
        await svc.Entrar(ana, new PontoEntrarRequest(), default);
        relogio.Avancar(TimeSpan.FromHours(5));
        await svc.Sair(ana, new PontoSairRequest(), default);

        Assert.Equal(2, equipe.Preenchidos.Count);
        Assert.Equal(("11:00", "23:00"), (equipe.Preenchidos[1].Entrada, equipe.Preenchidos[1].Saida));
        Assert.Equal(2, (await svc.Dia(ana, null, new DateOnly(2026, 11, 17), default)).Pontos.Count);
    }

    [DeliveryDatabaseFact]
    public async Task Permissoes_do_ponto_de_outra_pessoa()
    {
        await using var db = await Database.Create();
        var (svc, relogio, _, _) = db.Servico(Inicio);
        var ana = db.Ator(db.Ana);
        var gerente = new PontoAtor(db.A, 99, null, true);

        Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<PontoException>(() =>
            svc.Entrar(ana, new PontoEntrarRequest(db.Bruno), default))).Codigo);
        Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<PontoException>(() => svc.Abertos(ana, default))).Codigo);
        Assert.Equal("SEM_PERMISSAO", (await Assert.ThrowsAsync<PontoException>(() =>
            svc.Entrar(gerente, new PontoEntrarRequest(), default))).Codigo);

        // Bruno não tem função: o gerente escolhe o modo.
        Assert.Equal("VALIDACAO", (await Assert.ThrowsAsync<PontoException>(() =>
            svc.Entrar(gerente, new PontoEntrarRequest(db.Bruno), default))).Codigo);
        await svc.Entrar(gerente, new PontoEntrarRequest(db.Bruno, "cozinha"), default);
        Assert.Single(await svc.Abertos(gerente, default));
        relogio.Avancar(TimeSpan.FromMinutes(30));
        Assert.Equal("EXIGE_MOTIVO", (await Assert.ThrowsAsync<PontoException>(() =>
            svc.Sair(gerente, new PontoSairRequest(db.Bruno), default))).Codigo);
        var saiu = await svc.Sair(gerente, new PontoSairRequest(db.Bruno, "Esqueceu de bater"), default);
        Assert.Equal(("gerente", "Esqueceu de bater"), (saiu.Ponto.FechadoPor, saiu.Ponto.MotivoSaida));

        Assert.Equal("NAO_ENCONTRADO", (await Assert.ThrowsAsync<PontoException>(() =>
            svc.Entrar(new PontoAtor(db.B, 99, null, true), new PontoEntrarRequest(db.Ana), default))).Codigo);
    }

    // ---------- apoio ----------

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider
    {
        private DateTimeOffset _agora = agora;
        public override DateTimeOffset GetUtcNow() => _agora;
        public void Avancar(TimeSpan t) => _agora += t;
    }

    private sealed class EquipeFalsa(Database db, TimeZoneInfo fuso) : IPontoEquipe
    {
        public List<(Guid Pessoa, DateOnly Dia, string Entrada, string Saida)> Preenchidos { get; } = new();
        public Task<TimeZoneInfo> Fuso(Guid loja, CancellationToken ct) => Task.FromResult(fuso);

        public async Task PreencherTurno(Guid loja, int usuario, Guid pessoaId, DateOnly dia, string entrada, string saida, string? motivo,
            CancellationToken ct)
        {
            Preenchidos.Add((pessoaId, dia, entrada, saida));
            await db.Execute("""
                INSERT INTO gestao_turnos (id, estabelecimento_id, pessoa_id, data, entrada, saida, status, acontecimento_id)
                VALUES (gen_random_uuid(), @loja, @pessoaId, @dia, @entrada, @saida, 'realizado', gen_random_uuid())
                ON CONFLICT (estabelecimento_id, pessoa_id, data) DO UPDATE SET entrada = EXCLUDED.entrada, saida = EXCLUDED.saida
                """, new { loja, pessoaId, dia = dia.ToDateTime(TimeOnly.MinValue), entrada, saida });
        }
    }

    private sealed class Avisos : ConcurrentBag<Guid>, IPontoTempoReal
    {
        public Task Mudou(Guid loja, Guid pessoaId) { Add(pessoaId); return Task.CompletedTask; }
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public Guid Ana { get; } = Guid.NewGuid();
        public Guid Bruno { get; } = Guid.NewGuid();
        private readonly string _schema = "ponto_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public PontoAtor Ator(Guid pessoa) => new(A, pessoa == Ana ? 10 : 20, pessoa, false);

        public (PontoService, Relogio, EquipeFalsa, Avisos) Servico(DateTimeOffset agora, TimeZoneInfo? fuso = null)
        {
            var relogio = new Relogio(agora);
            var equipe = new EquipeFalsa(this, fuso ?? SaoPaulo);
            var avisos = new Avisos();
            return (new PontoService(Source, equipe, avisos, relogio), relogio, equipe, avisos);
        }

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            // Só o que a migração e o serviço tocam, no formato de 20261109_01.
            await db.Execute("""
                CREATE TABLE estabelecimentos (id uuid PRIMARY KEY);
                CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                CREATE TABLE gestao_pessoas (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos (id),
                  nome varchar(120) NOT NULL, vinculo varchar(12) NOT NULL, usuario_id integer, funcao_codigo text,
                  ativo boolean NOT NULL DEFAULT true, atualizado_em timestamptz NOT NULL DEFAULT now(), UNIQUE (estabelecimento_id, id));
                CREATE TABLE gestao_turnos (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, pessoa_id uuid NOT NULL, data date NOT NULL,
                  entrada varchar(5) NOT NULL, saida varchar(5) NOT NULL, status varchar(10) NOT NULL, acontecimento_id uuid,
                  UNIQUE (estabelecimento_id, pessoa_id, data));
                CREATE FUNCTION gestao_bloqueia_exclusao() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'sem exclusao'; END $$;
                INSERT INTO estabelecimentos VALUES (@A), (@B);
                INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, vinculo, usuario_id, funcao_codigo) VALUES
                  (@Ana, @A, 'Ana', 'fixo', 10, 'garcom'), (@Bruno, @A, 'Bruno', 'fixo', 20, NULL);
                """, new { db.A, db.B, db.Ana, db.Bruno });
            var migracao = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261204_01_gestao_ponto.sql"));
            await db.Execute(migracao);
            await db.Execute(migracao); // idempotente
            var verify = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261204_02_gestao_ponto_verify.sql"));
            await using (var cx = await db.Source.OpenConnectionAsync())
                Assert.All(await cx.QueryAsync<(string Item, bool Ok)>(verify), l => Assert.True(l.Ok, l.Item));
            return db;
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async Task<T> Scalar<T>(string sql, object? args = null)
        {
            await using var cx = await Source.OpenConnectionAsync();
            return (await cx.ExecuteScalarAsync<T>(sql, args))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base);
            await cx.OpenAsync();
            await cx.ExecuteAsync($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        }
    }
}
