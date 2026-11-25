using System.Text.Json;
using APIBack.Atendimento;
using APIBack.Infrastructure;
using APIBack.Options;
using APIBack.Social.Arquivos;
using APIBack.Social.Editor;
using APIBack.Social.Ia;
using APIBack.Social.Nucleo;
using APIBack.Social.Voz;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Social;

/// <summary>R10 com provedores falsos. Roda só com TEST_DELIVERY_DATABASE (PostgreSQL local, schema próprio descartado ao final).</summary>
public sealed class SocialVozDatabaseTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static GerarVozRequest Pedido(long corte, params (string Id, string Texto, string Voz)[] falas) =>
        new(corte, falas.Select(f => new FalaGerarRequest(f.Id, f.Texto, f.Voz, 1)).ToList());

    private static ClonarVozRequest Amostra(bool aceite = true) =>
        new(AudioInfo.WavSilencio(75m), "audio/wav", "minha-voz.wav", aceite, "Eu leio este texto com a minha própria voz.", null,
            "203.0.113.7", "Teste/1.0");

    [DeliveryDatabaseFact]
    public async Task Fala_repetida_vem_do_cache_e_nao_cobra_de_novo()
    {
        await using var db = await Database.Create();
        var corte = await db.Corte(db.LojaA);
        var r1 = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Olha o que vai acontecer agora", "fem"), ("f2", "E acabou", "doc")), Ct);
        Assert.Equal(2, r1.Falas.Count);
        Assert.All(r1.Falas, f => Assert.False(f.DoCache));
        Assert.Equal(1, r1.Creditos);
        Assert.Equal(99, r1.Restam);
        Assert.Equal(2, db.Fabrica.Sinteses);
        Assert.Equal(2, db.Storage.Enviados.Count);
        Assert.NotEmpty(r1.Falas[0].Palavras);
        Assert.StartsWith("https://r2.teste/ler/social/", r1.Falas[0].UrlLer);

        // Mesmo texto e voz, outro falaId: nada novo, nada cobrado. Uma fala nova no meio só cobra a nova.
        var r2 = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("x1", "Olha o que vai acontecer agora", "fem"), ("x2", "E acabou", "doc")), Ct);
        Assert.All(r2.Falas, f => Assert.True(f.DoCache));
        Assert.Equal(0, r2.Creditos);
        Assert.Equal(r1.Falas[0].ArquivoId, r2.Falas[0].ArquivoId);
        Assert.Equal(2, db.Fabrica.Sinteses);
        var r3 = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("y1", "E acabou", "doc"), ("y2", "E acabou", "fem")), Ct);
        Assert.True(r3.Falas[0].DoCache);
        Assert.False(r3.Falas[1].DoCache);
        Assert.Equal(3, db.Fabrica.Sinteses);

        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_uso_ia"));
        Assert.Equal(2, await db.Scalar<int>("SELECT usados FROM social_creditos"));
        Assert.Equal(3, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_arquivos WHERE origem = 'voz_ia' AND enviado_em IS NOT NULL"));
    }

    [DeliveryDatabaseFact]
    public async Task Sem_saldo_responde_402_com_o_que_falta_e_nao_gera_nada()
    {
        await using var db = await Database.Create(o => o.CreditosMesPadrao = 1);
        var corte = await db.Corte(db.LojaA);
        var longa = string.Join(' ', Enumerable.Repeat("oi", 200)); // 599 caracteres, ~77 s = 3 créditos
        var ex = await Assert.ThrowsAsync<SocialException>(() => db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", longa, "masc")), Ct));
        Assert.Equal(402, ex.Status);
        Assert.Equal("SEM_CREDITOS", ex.Codigo);
        Assert.Contains("\"falta\":2", JsonSerializer.Serialize(ex.Detalhes));
        Assert.Equal(0, db.Fabrica.Sinteses);
        Assert.Empty(db.Storage.Enviados);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_uso_ia"));

        var est = await db.Voz.EstimativaAsync(db.LojaA, new EstimativaVozRequest(new() { new(longa, 1) }), Ct);
        Assert.Equal(3, est.Creditos);
        Assert.Equal(1, est.Restam);
        Assert.False(est.Basta);
    }

    [DeliveryDatabaseFact]
    public async Task Cobranca_que_falha_na_transacao_apaga_o_que_subiu_para_o_r2()
    {
        await using var db = await Database.Create(o => o.CreditosMesPadrao = 1);
        var corte = await db.Corte(db.LojaA);
        // A conferência prévia passa (1 crédito previsto), mas outra aba gasta o crédito antes da cobrança.
        db.Fabrica.AntesDeSintetizar = () => db.Execute("SET zippy.sistema = 'sim'; UPDATE social_creditos SET usados = 1");
        var ex = await Assert.ThrowsAsync<SocialException>(() => db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Fala curta", "fem")), Ct));
        Assert.Equal(402, ex.Status);
        Assert.Single(db.Storage.Enviados);
        Assert.Equal(db.Storage.Enviados, db.Storage.Apagados);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_arquivos"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_voz_cache"));
    }

    [DeliveryDatabaseFact]
    public async Task Voz_clonada_exige_consentimento_guarda_a_prova_e_pode_ser_apagada()
    {
        await using var db = await Database.Create();
        var corte = await db.Corte(db.LojaA);

        var semAceite = await Assert.ThrowsAsync<SocialException>(() => db.Voz.ClonarAsync(db.LojaA, 1, Amostra(aceite: false), Ct));
        Assert.Equal("CONSENTIMENTO_OBRIGATORIO", semAceite.Codigo);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_voz_consentimentos"));
        var semVoz = await Assert.ThrowsAsync<SocialException>(() => db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Oi", "clone")), Ct));
        Assert.Equal("SEM_VOZ_CLONADA", semVoz.Codigo);

        var criada = await db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct);
        Assert.True(criada.Ativa);
        Assert.Equal(10, criada.Creditos);
        Assert.Equal(90, criada.Restam);
        Assert.Equal("ativa|203.0.113.7|75.00|" + TermoVozClonada.Versao, await db.Scalar<string>(
            "SELECT status || '|' || ip || '|' || audio_duracao || '|' || termo_versao FROM social_voz_consentimentos"));
        Assert.Equal(64, await db.Scalar<int>("SELECT length(audio_sha256) FROM social_voz_consentimentos"));
        Assert.Contains("não de outra pessoa", await db.Scalar<string>("SELECT termo_texto FROM social_voz_consentimentos"));

        var de_novo = await Assert.ThrowsAsync<SocialException>(() => db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct));
        Assert.Equal(409, de_novo.Status);
        // Outra pessoa da mesma loja pode ter a sua.
        Assert.True((await db.Voz.ClonarAsync(db.LojaA, 2, Amostra(), Ct)).Ativa);

        var fala = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Agora com a minha voz", "clone")), Ct);
        Assert.Single(fala.Falas);
        Assert.StartsWith("falsa-", db.Fabrica.UltimaVoz);

        var apagada = await db.Voz.ApagarCloneAsync(db.LojaA, 1, Ct);
        Assert.False(apagada.Ativa);
        Assert.Single(db.Fabrica.VozesApagadas);
        Assert.Equal("revogada", await db.Scalar<string>("SELECT status FROM social_voz_consentimentos WHERE usuario_id = 1"));
        Assert.Equal("SEM_VOZ_CLONADA", (await Assert.ThrowsAsync<SocialException>(() =>
            db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f2", "De novo", "clone")), Ct))).Codigo);
        // Depois de apagar, dá para gravar outra; a prova antiga continua guardada.
        Assert.True((await db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct)).Ativa);
        Assert.Equal(3, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_voz_consentimentos"));
    }

    [DeliveryDatabaseFact]
    public async Task Clone_que_falha_no_provedor_marca_a_prova_e_nao_cobra()
    {
        await using var db = await Database.Create();
        db.Fabrica.FalharClone = true;
        var ex = await Assert.ThrowsAsync<SocialException>(() => db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct));
        Assert.Equal("IA_INDISPONIVEL", ex.Codigo);
        Assert.Equal("falhou", await db.Scalar<string>("SELECT status FROM social_voz_consentimentos"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_uso_ia"));
        db.Fabrica.FalharClone = false;
        Assert.True((await db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct)).Ativa);
    }

    [DeliveryDatabaseFact]
    public async Task Lojas_nao_veem_cortes_falas_cache_nem_vozes_umas_das_outras()
    {
        await using var db = await Database.Create();
        var corteA = await db.Corte(db.LojaA);
        var corteB = await db.Corte(db.LojaB);
        await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corteA, ("f1", "Mesma fala", "fem")), Ct);
        await db.Voz.ClonarAsync(db.LojaA, 1, Amostra(), Ct);

        Assert.Equal(404, (await Assert.ThrowsAsync<SocialException>(() =>
            db.Voz.GerarAsync(db.LojaB, 1, Pedido(corteA, ("f1", "Mesma fala", "fem")), Ct))).Status);
        // Mesmo texto na loja B: não usa o cache da A, gera e cobra na B.
        var b = await db.Voz.GerarAsync(db.LojaB, 1, Pedido(corteB, ("f1", "Mesma fala", "fem")), Ct);
        Assert.False(b.Falas[0].DoCache);
        Assert.Equal(1, b.Creditos);
        Assert.Equal(99, b.Restam);
        Assert.False((await db.Voz.CloneAsync(db.LojaB, 1, Ct)).Ativa);
        Assert.Equal("SEM_VOZ_CLONADA", (await Assert.ThrowsAsync<SocialException>(() =>
            db.Voz.GerarAsync(db.LojaB, 1, Pedido(corteB, ("f2", "Oi", "clone")), Ct))).Codigo);

        // Row-level security: marcando a loja B, só aparece o que é da B.
        Assert.Equal(1, await db.NaLoja<int>(db.LojaB, "SELECT COUNT(*)::int FROM social_uso_ia"));
        Assert.Equal(1, await db.NaLoja<int>(db.LojaB, "SELECT COUNT(*)::int FROM social_voz_cache"));
        Assert.Equal(0, await db.NaLoja<int>(db.LojaB, "SELECT COUNT(*)::int FROM social_voz_consentimentos"));
        Assert.Equal(0, await db.NaLoja<int>(Guid.NewGuid(), "SELECT COUNT(*)::int FROM social_creditos"));
    }

    [DeliveryDatabaseFact]
    public async Task Uso_fica_registrado_com_provedor_modelo_unidades_e_custo_estimado()
    {
        // Com a chave da Zippy da OpenAI: o catálogo traduz a voz e dá o preço por 1000 caracteres.
        await using var db = await Database.Create(o => { o.FalsoSemChave = false; o.Chaves["openai"] = "sk-teste"; o.Chaves["anthropic"] = "sk-ant-teste"; });
        var corte = await db.Corte(db.LojaA);
        var texto = new string('a', 400) + " fim";
        await db.Voz.GerarAsync(db.LojaA, 7, Pedido(corte, ("f1", texto, "fem")), Ct);
        Assert.Equal("nova", db.Fabrica.UltimaVoz);
        Assert.Equal("7|voz|openai|gpt-4o-mini-tts|zippy|segundos|1|0.006060", await db.Scalar<string>("""
            SELECT usuario_id || '|' || uso || '|' || provedor_id || '|' || modelo || '|' || origem || '|' || unidade || '|' || creditos
                   || '|' || custo_estimado FROM social_uso_ia
            """));
        Assert.Equal(corte, await db.Scalar<long>("SELECT corte_id FROM social_uso_ia"));

        var roteiro = await db.Textos.RoteiroAsync(db.LojaA, 7, new RoteiroRequest(corte, "historia"), Ct);
        Assert.Equal(2, roteiro.Falas.Count);
        Assert.Equal("inicio", roteiro.Falas[0].Ancora);
        Assert.Equal(1, roteiro.Creditos);
        Assert.Equal("roteiro|tokens|140.00|1", await db.Scalar<string>(
            "SELECT uso || '|' || unidade || '|' || unidades || '|' || creditos FROM social_uso_ia WHERE uso = 'roteiro'"));
        Assert.Equal(3, (await db.Textos.GanchosAsync(db.LojaA, 7, new GanchosRequest(corte), Ct)).Ganchos.Count);
        Assert.NotEmpty((await db.Textos.EncurtarAsync(db.LojaA, 7, new EncurtarRequest("Uma fala bem comprida demais", 4), Ct)).Texto);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_uso_ia WHERE uso = 'textos'"));
        Assert.Equal(4, await db.Scalar<int>("SELECT usados FROM social_creditos"));
    }

    [DeliveryDatabaseFact]
    public async Task Chave_propria_que_falha_segue_o_ao_falhar_e_nunca_falha_calada()
    {
        await using var db = await Database.Create();
        var corte = await db.Corte(db.LojaA);
        await db.ChavePropria(db.LojaA, "elevenlabs", "usar_cota");
        db.Fabrica.ChavePropriaRecusada = true;

        var r = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Fala", "fem")), Ct);
        Assert.Equal("zippy", r.Origem);
        Assert.Contains("Sua chave do ElevenLabs falhou", r.Aviso);
        Assert.Equal(1, r.Creditos);
        Assert.Equal("recusada", await db.Scalar<string>("SELECT status FROM social_chaves"));

        await db.Execute("SET zippy.sistema = 'sim'; UPDATE social_config_ia SET ao_falhar = 'parar'");
        var parar = await Assert.ThrowsAsync<SocialException>(() => db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f2", "Outra", "fem")), Ct));
        Assert.Equal("CHAVE_PROPRIA_FALHOU", parar.Codigo);

        // Chave própria funcionando: não gasta crédito da Zippy, mas registra o uso.
        db.Fabrica.ChavePropriaRecusada = false;
        var propria = await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f3", "Mais uma", "fem")), Ct);
        Assert.Equal("propria", propria.Origem);
        Assert.Equal(0, propria.Creditos);
        Assert.Equal("chave-da-loja", db.Fabrica.UltimaChave);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM social_uso_ia WHERE origem = 'propria' AND creditos = 0"));
        Assert.Equal(1, await db.Scalar<int>("SELECT usados FROM social_creditos"));
    }

    [DeliveryDatabaseFact]
    public async Task Conta_em_atraso_e_teto_diario_protegem_a_cota_da_zippy()
    {
        await using var db = await Database.Create(o => o.TetoDiarioCreditos = 2);
        var corte = await db.Corte(db.LojaA);
        db.Conta.Atraso = true;
        Assert.Equal("CONTA_EM_ATRASO", (await Assert.ThrowsAsync<SocialException>(() =>
            db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Oi", "fem")), Ct))).Codigo);
        db.Conta.Atraso = false;

        await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f1", "Um", "fem")), Ct);
        await db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f2", "Dois", "fem")), Ct);
        var teto = await Assert.ThrowsAsync<SocialException>(() => db.Voz.GerarAsync(db.LojaA, 1, Pedido(corte, ("f3", "Três", "fem")), Ct));
        Assert.Equal("TETO_DIARIO_IA", teto.Codigo);
        Assert.Equal(429, teto.Status);
    }

    /// <summary>Provedores falsos que contam o que foi pedido e simulam falhas.</summary>
    private sealed class FabricaTeste : IFabricaVoz
    {
        public int Sinteses;
        public string? UltimaVoz, UltimaChave;
        public bool ChavePropriaRecusada, FalharClone;
        public Func<Task>? AntesDeSintetizar;
        public List<string> VozesApagadas { get; } = new();

        public IVoz Criar(ProvedorEscolhido p) => new Voz(this, p);

        private sealed class Voz(FabricaTeste f, ProvedorEscolhido p) : IVoz
        {
            private readonly VozFalsa _falsa = new();
            public string Provedor => p.ProvedorId;
            public CapacidadesVoz Capacidades => _falsa.Capacidades;

            public async Task<AudioVoz> SintetizarAsync(PedidoVoz pedido, CancellationToken ct)
            {
                if (p.Origem == OrigensIa.Propria && f.ChavePropriaRecusada)
                    throw new FalhaIaException(p.ProvedorId, "chave recusada", TipoFalhaIa.ChaveRecusada);
                if (f.AntesDeSintetizar is { } antes) await antes();
                f.Sinteses++;
                f.UltimaVoz = pedido.Voz;
                f.UltimaChave = p.Chave;
                return await _falsa.SintetizarAsync(pedido, ct);
            }

            public Task<string> CriarVozClonadaAsync(AmostraVoz amostra, ConsentimentoVoz c, CancellationToken ct) =>
                f.FalharClone ? throw new FalhaIaException(p.ProvedorId, "fora", TipoFalhaIa.ForaDoAr) : _falsa.CriarVozClonadaAsync(amostra, c, ct);

            public Task ApagarVozClonadaAsync(string vozId, CancellationToken ct)
            {
                f.VozesApagadas.Add(vozId);
                return Task.CompletedTask;
            }
        }
    }

    private sealed class ContaTeste : IContaIa
    {
        public bool Atraso;
        public Task<bool> EmAtrasoAsync(Guid loja, CancellationToken ct) => Task.FromResult(Atraso);
    }

    private sealed class ArmazenamentoTeste : IArmazenamento
    {
        public List<string> Enviados { get; } = new();
        public List<string> Apagados { get; } = new();
        public bool Configurado => true;
        public string UrlParaEnviar(string key, string mime, long bytes, TimeSpan validade) => $"https://r2.teste/enviar/{key}";
        public string UrlParaLer(string key, TimeSpan validade) => $"https://r2.teste/ler/{key}";
        public Task ApagarAsync(string key, CancellationToken ct) { Apagados.Add(key); return Task.CompletedTask; }
        public Task<long?> TamanhoAsync(string key, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task EnviarAsync(string key, byte[] bytes, string mime, CancellationToken ct) { Enviados.Add(key); return Task.CompletedTask; }
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public ArmazenamentoTeste Storage { get; } = new();
        public FabricaTeste Fabrica { get; } = new();
        public ContaTeste Conta { get; } = new();
        public SocialVozService Voz { get; private set; } = null!;
        public SocialTextosService Textos { get; private set; } = null!;
        public TokenProtector Cifra { get; } = new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Atendimento:ChaveToken"] = "chave-de-teste-do-social" }).Build());
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        public string Schema { get; } = "social_voz_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public static async Task<Database> Create(Action<SocialIaOptions>? ajustar = null)
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
                INSERT INTO usuario VALUES (1, 'Maria'), (2, 'João'), (7, 'Ana');
                """, new { A = db.LojaA, B = db.LojaB });
            foreach (var m in new[] { "20261208_01_social_editor_base.sql", "20261208_03_social_ia_voz.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));

            var op = new SocialIaOptions { FalsoSemChave = true };
            ajustar?.Invoke(op);
            var ia = new SocialIa(db.Source, Microsoft.Extensions.Options.Options.Create(op), new ConfigurationBuilder().Build(), db.Cifra,
                db.Conta, new LimiteIaZippy(TimeProvider.System));
            db.Voz = new SocialVozService(db.Source, ia, db.Fabrica, db.Storage, NullLogger<SocialVozService>.Instance);
            db.Textos = new SocialTextosService(db.Source, ia, new FabricaTextoTeste());
            return db;
        }

        private sealed class FabricaTextoTeste : IFabricaTexto
        {
            public IModeloTexto Criar(ProvedorEscolhido p) => new TextoFalso();
        }

        public async Task<long> Corte(Guid loja) =>
            (await new SocialEditorService(Source, Storage).CriarExemploAsync(loja, 1, Ct)).CorteIds[0];

        public async Task ChavePropria(Guid loja, string provedor, string aoFalhar)
        {
            var cifrada = Cifra.Proteger("chave-da-loja");
            await Execute("""
                SET zippy.sistema = 'sim';
                INSERT INTO social_chaves (estabelecimento_id, provedor_id, chave_cifrada, final4) VALUES (@loja, @provedor, @cifrada, 'loja');
                INSERT INTO social_config_ia (estabelecimento_id, uso, origem, provedor_id, ao_falhar)
                VALUES (@loja, 'voz', 'propria', @provedor, @aoFalhar);
                """, new { loja, provedor, cifrada, aoFalhar });
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async Task<T?> Scalar<T>(string sql)
        {
            await using var cx = await Source.OpenConnectionAsync();
            await cx.ExecuteAsync("SET zippy.sistema = 'sim'");
            return await cx.ExecuteScalarAsync<T>(sql);
        }

        public Task<T?> NaLoja<T>(Guid loja, string sql) =>
            SocialBanco.NaLoja(Source, loja, Ct, (c, tx) => c.ExecuteScalarAsync<T>(new CommandDefinition(sql, transaction: tx)));

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {Schema} CASCADE");
        }
    }
}
