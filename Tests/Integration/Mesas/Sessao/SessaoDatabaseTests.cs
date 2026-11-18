using APIBack.Mesas.Auditoria;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Planta;
using APIBack.Mesas.Sessao;
using APIBack.Tests.Unit.Mesas.Planta;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Mesas.Sessao;

/// <summary>Sessão da mesa, chamados e auditoria (P07), num schema isolado do PostgreSQL local. Mesas 1, 2 (4 lugares) e 3 (2 lugares).</summary>
public sealed class SessaoDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Roteiro_do_dono_abrir_sentar_juntar_transferir_encerrar_liberar_e_auditoria_com_autor()
    {
        await using var db = await Database.Create();
        var (m1, m2, m3) = (db.Mesa("1"), db.Mesa("2"), db.Mesa("3"));

        // Rafa (garçom com ponto) abre a mesa 1 para 3 pessoas; quem abriu e quem atende ficam registrados.
        var aberta = await db.Sessao.AbrirMesaAsync(db.Garcom, new(K(), m1, 1, 3, null, null, null, null, null), default);
        Assert.Equal(2, aberta.Versoes[m1]);
        Assert.Equal(db.RafaPessoa, await db.Escalar<Guid>("SELECT garcom_id FROM mesa_sessao WHERE id = @id", new { id = aberta.SessaoId }));
        Assert.Equal("Rafa", await db.Escalar<string>("SELECT aberta_por FROM mesa_sessao WHERE id = @id", new { id = aberta.SessaoId }));
        Assert.True(aberta.QrToken.Length >= 22);

        // Ana (gerente) põe três pessoas nas cadeiras; a contagem anônima vai sendo trocada pelos nomes.
        var nomes = new[] { "Lucas", "Júlia", "Bia" };
        var pessoas = new List<Guid>();
        for (var i = 0; i < 3; i++)
            pessoas.Add((await db.Sessao.IncluirPessoaAsync(db.Gerente, new(K(), m1, nomes[i], null, $"m-peq-1:c{i + 1}", null, null, null), default)).PessoaId);
        Assert.Equal(0, await db.Escalar<int>("SELECT ocupantes_anonimos FROM mesa_sessao WHERE id = @id", new { id = aberta.SessaoId }));
        var ocupada = await Assert.ThrowsAsync<MesasException>(() =>
            db.Sessao.SentarAsync(db.Gerente, new(K(), pessoas[0], "m-peq-1:c2"), default));
        Assert.Equal("JA_FEITO", ocupada.Codigo);
        Assert.Contains("Júlia", ocupada.Message);
        await db.Sessao.TrocarLugaresAsync(db.Gerente, new(K(), pessoas[0], pessoas[1]), default);
        Assert.Equal("m-peq-1:c2", await db.Escalar<string>("SELECT cadeira_id FROM mesa_pessoa WHERE id = @id", new { id = pessoas[0] }));

        // Junta 1 + 2 e transfere a 2 para a 3: a sessão segue com a 1 e a 3.
        var resumo1 = await db.Salao.MesaAsync(db.A, m1, default);
        var juntas = await db.Sessao.JuntarMesasAsync(db.Gerente, new(K(), new[] { m1, m2 },
            new Dictionary<Guid, int> { [m1] = resumo1.Versao, [m2] = 1 }, null), default);
        Assert.Equal(aberta.SessaoId, juntas.SessaoId);
        Assert.Equal(m1, juntas.MesaPrincipalId);
        var salao = await db.Salao.SalaoAsync(db.A, null, default);
        Assert.Equal(m1, salao.First(x => x.MesaId == m2).JuntaCom);
        Assert.Equal(3, salao.First(x => x.MesaId == m1).QtdPessoas);

        var transf = await db.Sessao.TransferirMesaAsync(db.Gerente, new(K(), m2, juntas.Versoes[m2], m3, null, "Cliente pediu janela"), default);
        Assert.Equal(m3, transf.MesaId);
        Assert.Equal("aguardando_limpeza", await db.Status(m2));
        Assert.Equal(new[] { m1, m3 }.OrderBy(x => x), (await db.Salao.MesaAsync(db.A, m1, default)).Mesas.Select(x => x.MesaId).OrderBy(x => x));

        // Dono encerra, a 1 e a 3 vão para limpeza e o garçom libera as duas.
        var antesDeEncerrar = await db.Salao.MesaAsync(db.A, m1, default);
        var fim = await db.Sessao.EncerrarAsync(db.Dono, new(K(), m1, antesDeEncerrar.Versao, null, null), default);
        Assert.Equal("aguardando_limpeza", await db.Status(m1));
        Assert.Equal("aguardando_limpeza", await db.Status(m3));
        Assert.Equal("encerrada", await db.Escalar<string>("SELECT estado FROM mesa_sessao WHERE id = @id", new { id = aberta.SessaoId }));
        Assert.Equal(0, await db.Escalar<int>("SELECT count(*) FROM mesa_comanda WHERE sessao_id = @id AND aberta", new { id = aberta.SessaoId }));
        foreach (var (mesa, v) in fim.Versoes)
            await db.Sessao.LiberarLimpezaAsync(db.Garcom, new(K(), mesa, v), default);
        await db.Sessao.LiberarLimpezaAsync(db.Garcom, new(K(), m2, await db.Escalar<int>("SELECT versao FROM mesa_estado WHERE mesa_id = @m2", new { m2 })), default);
        Assert.All(await db.Salao.SalaoAsync(db.A, null, default), m => Assert.Equal("livre", m.Status));

        // Cada passo na auditoria, com o autor certo.
        var aud = await db.Auditoria.ListarAsync(db.A, new(null, null, null, null, null, null, null, 200), default);
        string Autor(string acao) => aud.Itens.First(e => e.Acao == acao).Ator.Nome;
        Assert.Equal("Rafa", Autor("abrirMesa"));
        Assert.Equal("Ana", Autor("incluirPessoa"));
        Assert.Equal("Ana", Autor("juntarMesas"));
        Assert.Equal("Ana", Autor("transferirMesa"));
        Assert.Equal("Dono", Autor("encerrar"));
        Assert.Equal("Rafa", Autor("liberarLimpeza"));
        Assert.Equal("Cliente pediu janela", aud.Itens.First(e => e.Acao == "transferirMesa").Motivo);
        Assert.Equal(3, aud.Itens.Count(e => e.Acao == "incluirPessoa"));

        // Filtros: por quem (id do usuário), por ação e por mesa; paginado com cursor.
        var doRafa = await db.Auditoria.ListarAsync(db.A, new("30", null, null, null, null, null, null, null), default);
        Assert.All(doRafa.Itens, e => Assert.Equal("30", e.Ator.Id));
        var daMesa2 = await db.Auditoria.ListarAsync(db.A, new(null, "juntarMesas,transferirMesa", null, null, m2, null, null, null), default);
        Assert.Single(daMesa2.Itens); // a junção é registrada na principal; a transferência tem a 2 como alvo
        var p1 = await db.Auditoria.ListarAsync(db.A, new(null, null, null, null, null, null, null, 3), default);
        var p2 = await db.Auditoria.ListarAsync(db.A, new(null, null, null, null, null, null, p1.Cursor, 3), default);
        Assert.NotNull(p1.Cursor);
        Assert.Empty(p1.Itens.Select(x => x.Id).Intersect(p2.Itens.Select(x => x.Id)));
        Assert.True(p1.Itens[^1].Quando >= p2.Itens[0].Quando);
        Assert.Empty((await db.Auditoria.ListarAsync(db.B, new(null, null, null, null, null, null, null, null), default)).Itens);
    }

    [DeliveryDatabaseFact]
    public async Task Versao_velha_devolve_409_com_o_estado_atual_e_corrida_so_abre_uma_vez()
    {
        await using var db = await Database.Create();
        var m1 = db.Mesa("1");

        var corrida = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Tentar(() =>
            db.Sessao.AbrirMesaAsync(db.Gerente, new(K(), m1, 1, 2, null, null, null, null, null), default))));
        Assert.Single(corrida, r => r is null);
        Assert.Single(corrida, r => r == "JA_FEITO");
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_sessao"));

        await db.Sessao.IncluirPessoaAsync(db.Gerente, new(K(), m1, "Ana", null, null, null, null, null), default); // versão 3
        var velha = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.EncerrarAsync(db.Dono, new(K(), m1, 2, null, null), default));
        Assert.Equal("VERSAO_VELHA", velha.Codigo);
        Assert.Equal(409, velha.Status);
        Assert.Equal(3, Prop<int>(velha.Detalhes, "versaoAtual"));
        Assert.Equal("ocupada", Prop<string>(velha.Detalhes, "status"));
        Assert.Equal("Ana", Prop<string>(velha.Detalhes, "porQuem"));
        Assert.Equal("ocupada", await db.Status(m1)); // nada mudou
    }

    [DeliveryDatabaseFact]
    public async Task Mesma_chave_devolve_a_mesma_resposta_e_chave_com_outro_corpo_e_recusada()
    {
        await using var db = await Database.Create();
        var m1 = db.Mesa("1");
        var chave = K();
        var pedido = new AbrirMesaRequest(chave, m1, 1, 4, null, null, "Júlia Mendes", "(11) 98888-7777", null);
        var a = await db.Sessao.AbrirMesaAsync(db.Gerente, pedido, default);
        var b = await db.Sessao.AbrirMesaAsync(db.Gerente, pedido, default);
        Assert.Equal(a.SessaoId, b.SessaoId);
        Assert.Equal(a.EventoId, b.EventoId);
        Assert.Equal(a.Versoes[m1], b.Versoes[m1]);
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_evento WHERE acao = 'abrirMesa'"));
        Assert.Equal(2, await db.Escalar<int>("SELECT versao FROM mesa_estado WHERE mesa_id = @m1", new { m1 }));
        Assert.Equal("+5511988887777", await db.Escalar<string>("SELECT celular FROM mesa_pessoa WHERE titular"));
        Assert.Equal(3, await db.Escalar<int>("SELECT ocupantes_anonimos FROM mesa_sessao"));

        var outra = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.AbrirMesaAsync(db.Gerente, pedido with { Pessoas = 5 }, default));
        Assert.Equal("CHAVE_REUSADA", outra.Codigo);

        // Erro não queima a chave: corrigido o pedido, a mesma chave executa.
        var c2 = K();
        var m2 = db.Mesa("2");
        var semVersao = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.LiberarLimpezaAsync(db.Gerente, new(c2, m2, null), default));
        Assert.Equal("ESTADO_INVALIDO", semVersao.Codigo);
        Assert.Equal(0, await db.Escalar<int>("SELECT count(*) FROM mesa_comando WHERE chave = @c2", new { c2 }));

        // Mesmo cliente pelo celular de novo: devolve a mesma pessoa.
        var volta = await db.Sessao.IncluirPessoaAsync(db.Gerente, new(K(), m1, "Júlia", "11988887777", null, null, null, null), default);
        Assert.True(volta.Retorno);
    }

    [DeliveryDatabaseFact]
    public async Task Transicoes_invalidas_e_motivo_obrigatorio()
    {
        await using var db = await Database.Create();
        var (m1, m2, m3) = (db.Mesa("1"), db.Mesa("2"), db.Mesa("3"));
        await db.Execute("UPDATE mesa_estado SET status = 'indisponivel', motivo_bloqueio = 'Quebrada' WHERE mesa_id = @m3", new { m3 });

        Assert.Equal("ESTADO_INVALIDO", await Codigo(() => db.Sessao.AbrirMesaAsync(db.Gerente, new(K(), m3, 1, 2, null, null, null, null, null), default)));
        Assert.Equal("ESTADO_INVALIDO", await Codigo(() => db.Sessao.LiberarLimpezaAsync(db.Gerente, new(K(), m1, 1), default)));
        Assert.Equal("ESTADO_INVALIDO", await Codigo(() => db.Sessao.ReabrirAsync(db.Gerente, new(K(), m1, 1, "Engano"), default)));
        Assert.Equal("ESTADO_INVALIDO", await Codigo(() => db.Sessao.ChamarGarcomAsync(db.Gerente, new(K(), m1, null, null, null), default)));

        var a = await db.Sessao.AbrirMesaAsync(db.Gerente, new(K(), m1, 1, 2, null, null, null, null, null), default);
        Assert.Equal("JA_FEITO", await Codigo(() => db.Sessao.SepararMesasAsync(db.Gerente, new(K(), m1, a.Versoes[m1]), default)));
        Assert.Equal("EXIGE_MOTIVO", await Codigo(() => db.Sessao.TransferirMesaAsync(db.Gerente, new(K(), m1, a.Versoes[m1], m2, null, null), default)));
        Assert.Equal("ESTADO_INVALIDO", await Codigo(() => db.Sessao.JuntarMesasAsync(db.Gerente, new(K(), new[] { m1, m3 }, null, 2), default)));
        Assert.Equal("SEM_PERMISSAO", await Codigo(() => db.Sessao.EncerrarAsync(db.Garcom, new(K(), m1, a.Versoes[m1], true, "Saiu sem pagar"), default)));

        // Chamado: abre, repetir é conflito, a caminho, atender; atender de novo diz quem atendeu.
        var ch = await db.Sessao.ChamarGarcomAsync(db.Gerente, new(K(), m1, null, null, "Mais gelo"), default);
        Assert.Equal("JA_FEITO", await Codigo(() => db.Sessao.ChamarGarcomAsync(db.Gerente, new(K(), m1, null, null, null), default)));
        Assert.Contains("chamado", (await db.Salao.MesaAsync(db.A, m1, default)).Alertas);
        await db.Sessao.CaminhoChamadoAsync(db.Garcom, new(K(), ch.ChamadoId, null), default);
        Assert.Equal(1, (await db.Sessao.AtenderChamadoAsync(db.Garcom, new(K(), null, m1), default)).Atendidos);
        var de2 = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.AtenderChamadoAsync(db.Gerente, new(K(), ch.ChamadoId, null), default));
        Assert.Equal("JA_FEITO", de2.Codigo);
        Assert.Contains("Rafa", de2.Message);

        // Conta pedida, encerrar, encerrar de novo (já feito) e reabrir só com motivo; reabrir devolve as pessoas.
        await db.Sessao.IncluirPessoaAsync(db.Gerente, new(K(), m1, "Ana", null, null, null, null, null), default);
        await db.Sessao.PedirContaAsync(db.Gerente, new(K(), m1, null), default);
        Assert.Equal("aguardando_pagamento", await db.Status(m1));
        var v = await db.Escalar<int>("SELECT versao FROM mesa_estado WHERE mesa_id = @m1", new { m1 });
        var fim = await db.Sessao.EncerrarAsync(db.Gerente, new(K(), m1, v, null, null), default);
        Assert.Equal("JA_FEITO", await Codigo(() => db.Sessao.EncerrarAsync(db.Gerente, new(K(), m1, fim.Versoes[m1], null, null), default)));
        Assert.Equal("EXIGE_MOTIVO", await Codigo(() => db.Sessao.ReabrirAsync(db.Gerente, new(K(), m1, fim.Versoes[m1], null), default)));
        await db.Sessao.ReabrirAsync(db.Gerente, new(K(), m1, fim.Versoes[m1], "Esqueceram a sobremesa"), default);
        Assert.Equal("aguardando_pagamento", await db.Status(m1));
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_pessoa WHERE saiu_em IS NULL"));
        Assert.True(await db.Escalar<bool>("SELECT ocorrencia FROM mesa_evento WHERE acao = 'reabrir'"));
    }

    [DeliveryDatabaseFact]
    public async Task Garcom_sem_ponto_e_autonomia_que_pede_o_gerente()
    {
        await using var db = await Database.Create();
        var (m1, m2) = (db.Mesa("1"), db.Mesa("2"));
        await db.Execute("UPDATE gestao_ponto SET saida = NOW()");
        Assert.Equal("SEM_PONTO", await Codigo(() => db.Sessao.AbrirMesaAsync(db.Garcom, new(K(), m1, 1, 2, null, null, null, null, null), default)));
        Assert.Equal("SEM_PONTO", await Codigo(() =>
            db.Sessao.AbrirMesaAsync(db.Gerente, new(K(), m1, 1, 2, db.RafaPessoa, null, null, null, null), default)));
        await db.Execute("INSERT INTO gestao_ponto VALUES (gen_random_uuid(), @A, @p, NOW(), NULL)", new { db.A, p = db.RafaPessoa });

        await db.Execute("""
            INSERT INTO mesas_config (estabelecimento_id, ativo, dados) VALUES (@A, true, '{"autonomiaGarcom":{"regras":{"juntarMesas":"gerente"}}}')
            ON CONFLICT (estabelecimento_id) DO UPDATE SET dados = EXCLUDED.dados
            """, new { db.A });
        var a = await db.Sessao.AbrirMesaAsync(db.Garcom, new(K(), m1, 1, 2, null, null, null, null, null), default);
        var chave = K();
        var pedido = new JuntarMesasRequest(chave, new[] { m1, m2 }, new Dictionary<Guid, int> { [m1] = a.Versoes[m1], [m2] = 1 }, null);
        var espera = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.JuntarMesasAsync(db.Garcom, pedido, default));
        Assert.Equal("AGUARDANDO_GERENTE", espera.Codigo);
        Assert.Equal(202, espera.Status);
        var repete = await Assert.ThrowsAsync<MesasException>(() => db.Sessao.JuntarMesasAsync(db.Garcom, pedido, default));
        Assert.Equal(Prop<Guid>(espera.Detalhes, "solicitacaoId"), Prop<Guid>(repete.Detalhes, "solicitacaoId"));
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_solicitacao WHERE estado = 'pendente' AND tipo = 'juntarMesas'"));
        Assert.Equal("livre", await db.Status(m2));
        Assert.Contains("autorizacao", (await db.Salao.MesaAsync(db.A, m1, default)).Alertas);

        // O gerente junta direto (a regra só vale para o garçom).
        await db.Sessao.JuntarMesasAsync(db.Gerente, pedido with { Chave = K() }, default);
        Assert.Equal("ocupada", await db.Status(m2));
    }

    [DeliveryDatabaseFact]
    public async Task Auditoria_e_imutavel()
    {
        await using var db = await Database.Create();
        await db.Sessao.AbrirMesaAsync(db.Gerente, new(K(), db.Mesa("1"), 1, 2, null, null, null, null, null), default);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("UPDATE mesa_evento SET ator_nome = 'Outro'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("DELETE FROM mesa_evento"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute("DELETE FROM mesa_sessao"));
        Assert.Equal("Ana", await db.Escalar<string>("SELECT ator_nome FROM mesa_evento WHERE acao = 'abrirMesa'"));
    }

    // ---------- apoio ----------

    private static Guid K() => Guid.NewGuid();

    private static async Task<string?> Tentar(Func<Task> f)
    {
        try { await f(); return null; }
        catch (MesasException ex) { return ex.Codigo; }
    }

    private static async Task<string> Codigo(Func<Task> f) => await Tentar(f) ?? "OK";

    private static T Prop<T>(object? o, string nome) => (T)o!.GetType().GetProperty(nome)!.GetValue(o)!;

    private sealed class TempoRealFalso : IMesasTempoReal, IPlantaTempoReal
    {
        public Task MesaMudou(Guid loja, Guid mesaId, int versao) => Task.CompletedTask;
        public Task SalaoMudou(Guid loja, int versaoPlanta) => Task.CompletedTask;
        public Task PendenciasMudou(Guid loja, string tipo, Guid? garcomId) => Task.CompletedTask;
        public Task Aviso(Guid loja, string tipo, Guid id, Guid? mesaId, Guid? garcomId) => Task.CompletedTask;
        public Task SolicitacaoMudou(Guid loja, Guid id, string estado) => Task.CompletedTask;
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public SessaoService Sessao { get; private set; } = null!;
        public SalaoService Salao { get; private set; } = null!;
        public AuditoriaService Auditoria { get; private set; } = null!;
        public Guid A { get; } = Guid.NewGuid();
        public Guid B { get; } = Guid.NewGuid();
        public Guid RafaPessoa { get; } = Guid.NewGuid();
        public MesasContexto Gerente => new(A, 10, MesasPermissoes.PapelGerente);
        public MesasContexto Dono => new(A, 20, MesasPermissoes.PapelDono);
        public MesasContexto Garcom => new(A, 30, MesasPermissoes.PapelGarcom);
        private readonly Dictionary<string, Guid> _mesas = new();
        private readonly string _schema = "mesas_sessao_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public Guid Mesa(string numero) => _mesas[numero];

        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            var tempo = new TempoRealFalso();
            db.Sessao = new SessaoService(new MesasComandos(db.Source, tempo), Array.Empty<IMesasSessaoGancho>());
            db.Salao = new SalaoService(db.Source, Array.Empty<IMesasAlertaFonte>());
            db.Auditoria = new AuditoriaService(db.Source);
            await db.Execute("""
                CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text, modulos_ativos text[]);
                CREATE TABLE usuario (id integer PRIMARY KEY, nome text);
                CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
                CREATE TABLE gestao_pessoas (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos (id),
                  nome varchar(120) NOT NULL, funcao varchar(60) NOT NULL DEFAULT '', vinculo varchar(12) NOT NULL,
                  usuario_id integer REFERENCES usuario (id), ativo boolean NOT NULL DEFAULT true,
                  atualizado_por integer, atualizado_em timestamptz NOT NULL DEFAULT now());
                CREATE TABLE gestao_ponto (id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, pessoa_id uuid NOT NULL,
                  entrada timestamptz NOT NULL, saida timestamptz NULL);
                CREATE FUNCTION gestao_bloqueia_alteracao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'imutavel'; END $$;
                CREATE FUNCTION gestao_bloqueia_exclusao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'sem exclusao'; END $$;
                INSERT INTO estabelecimentos VALUES (@A, 'Loja A', ARRAY['MESAS']), (@B, 'Loja B', ARRAY['MESAS']);
                INSERT INTO usuario VALUES (10, 'Ana'), (20, 'Dono'), (30, 'Rafa');
                """, new { db.A, db.B });
            foreach (var arquivo in new[] { "20261203_03_mesas_nucleo.sql", "20261205_05_mesas_planta.sql", "20261206_01_mesas_sessao.sql", "20261206_01_mesas_sessao.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", arquivo)));
            await db.Execute("""
                INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, vinculo, usuario_id, funcao_codigo) VALUES (@p, @A, 'Rafa Lima', 'clt', 30, 'garcom');
                INSERT INTO gestao_ponto VALUES (gen_random_uuid(), @A, @p, NOW() - INTERVAL '1 hour', NULL);
                """, new { db.A, p = db.RafaPessoa });

            var planta = new PlantaService(db.Source, tempo);
            var salvo = await planta.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(), null), default);
            await planta.PublicarAsync(db.Gerente, new(null, salvo.VersaoRascunho), default);
            await using var c = await db.Source.OpenConnectionAsync();
            foreach (var (numero, id) in await c.QueryAsync<(string, Guid)>("SELECT numero, id FROM mesa")) db._mesas[numero] = id;
            return db;
        }

        public Task<string> Status(Guid mesaId) => Escalar<string>("SELECT status FROM mesa_estado WHERE mesa_id = @mesaId", new { mesaId });

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
