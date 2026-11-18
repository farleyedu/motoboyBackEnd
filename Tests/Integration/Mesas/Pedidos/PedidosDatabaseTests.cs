using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Pedidos;
using APIBack.Mesas.Planta;
using APIBack.Mesas.Sessao;
using APIBack.Tests.Unit.Mesas.Planta;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration.Mesas.Pedidos;

/// <summary>
/// Pedidos, cozinha e entrega (P08) num schema isolado do PostgreSQL local. Rafa e Lia são garçons com ponto; Marina é gerente de salão.
/// Cardápio: Picanha (cozinha), Bruschetta (entrada, cozinha), Chope (bar), Vinho (bar, R$ 180).
/// </summary>
public sealed class PedidosDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Roteiro_do_dono_pedido_do_celular_aprovacao_cozinha_pronto_peguei_entreguei()
    {
        await using var db = await Database.Create();
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);

        // Pedido "do celular": o gerente simula a origem cliente (P18 fará pelo QR). Regra r2: passa pelo garçom.
        var chave = Guid.NewGuid();
        var req = new LancarPedidoRequest(chave, mesa, new[] { new ItemNovo(db.Picanha, 1, "ao ponto", null, null, null, null, null) }, null, null, "cliente", null);
        var lancado = await db.Pedidos.LancarAsync(db.Gerente, req, default);
        Assert.Equal(1, lancado.AguardandoAprovacao);
        Assert.Equal("aguardando_aprovacao", await db.Escalar<string>("SELECT estado FROM mesa_pedido WHERE id = @id", new { id = lancado.PedidoId }));
        var repetido = await db.Pedidos.LancarAsync(db.Gerente, req, default);                 // mesma chave: mesma resposta, nada novo
        Assert.Equal(lancado.PedidoId, repetido.PedidoId);
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_pedido"));

        var item = lancado.ItemIds[0];
        await db.Pedidos.AprovarAsync(db.Rafa, new(Guid.NewGuid(), null, null, lancado.PedidoId, null, null), default);
        Assert.Equal("em_fila", await db.Estado(item));
        Assert.Equal("enviado", await db.Escalar<string>("SELECT estado FROM mesa_pedido WHERE id = @id", new { id = lancado.PedidoId }));
        var segunda = await Erro(() => db.Pedidos.AprovarAsync(db.Lia, new(Guid.NewGuid(), null, null, lancado.PedidoId, null, null), default));
        Assert.Equal("JA_FEITO", segunda.Codigo);
        Assert.Contains("Rafa", segunda.Message);

        var fila = await db.Cozinha.CozinhaAsync(db.Gerente, "cozinha", default);
        Assert.Equal("novos", fila.Cartoes.Single().Coluna);
        Assert.Equal(1m, (await db.Cozinha.TotalAsync(db.Gerente, "cozinha", default)).Single().Qtd);

        Assert.Equal("em_preparo", (await db.Pedidos.AvancarEtapaAsync(db.Gerente, new(Guid.NewGuid(), item, null), default)).Estado);
        Assert.Equal("pronto", (await db.Pedidos.MarcarItemProntoAsync(db.Gerente, new(Guid.NewGuid(), item, null), default)).Estado);
        var paraLevar = await db.Cozinha.GarcomAsync(db.Rafa, null, default);
        Assert.Equal(item, paraLevar.ParaLevar.Single().ItemId);

        await db.Pedidos.PegarPratoAsync(db.Rafa, new(Guid.NewGuid(), item, null, null, null, null), default);
        Assert.Single((await db.Cozinha.GarcomAsync(db.Rafa, null, default)).ACaminho);
        var entregue = await db.Pedidos.EntregarAsync(db.Rafa, new(Guid.NewGuid(), item, null, null, null, null), default);
        Assert.Equal(1, entregue.Quantos);
        Assert.Equal("entregue", await db.Estado(item));
        Assert.False(await db.Escalar<bool>("SELECT entrega_direta OR entrega_presumida FROM mesa_item WHERE id = @item", new { item }));
        Assert.Equal("em_atendimento", await db.Escalar<string>("SELECT status FROM mesa_estado WHERE mesa_id = @mesa", new { mesa }));
        Assert.Equal(new[] { "lancarPedido", "aprovarPedido", "avancarEtapa", "marcarItemPronto", "pegarPrato", "entregar" },
            await db.Lista<string>("SELECT comando FROM mesa_item_evento WHERE item_id = @item ORDER BY quando, comando = 'entregar'", new { item }));
    }

    [DeliveryDatabaseFact]
    public async Task Cortesia_pede_o_gerente_e_o_garcom_segue_trabalhando()
    {
        await using var db = await Database.Create();
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var item = (await db.Lancar(db.Rafa, mesa, db.Chope)).ItemIds[0];
        var versao = await db.Versao(mesa);

        var e = await Erro(() => db.Pedidos.CortesiaAsync(db.Rafa, new(Guid.NewGuid(), item, versao, "Cliente esperou demais"), default));
        Assert.Equal(("AGUARDANDO_GERENTE", 202), (e.Codigo, e.Status));
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_solicitacao WHERE estado = 'pendente' AND tipo = 'cortesia' AND item_id = @item", new { item }));
        Assert.Null(await db.Escalar<string?>("SELECT cortesia::text FROM mesa_item WHERE id = @item", new { item }, aceitaNulo: true));
        Assert.Equal(0, await db.Escalar<int>("SELECT count(*) FROM mesa_aviso_gestor"));
        var leitura = await db.Cozinha.CozinhaAsync(db.Gerente, null, default);
        Assert.True(leitura.Cartoes.SelectMany(c => c.Itens).Single(i => i.Id == item).EsperandoGerente);

        // O garçom não fica travado: lança mais e entrega.
        var outro = await db.Lancar(db.Rafa, mesa, db.Picanha);
        await db.Pedidos.EntregarAsync(db.Rafa, new(Guid.NewGuid(), item, null, null, null, null), default);
        Assert.Equal("entregue", await db.Estado(item));
        Assert.Equal("em_fila", await db.Estado(outro.ItemIds[0]));

        // O gerente dá direto.
        var aviso = await db.Pedidos.CortesiaAsync(db.Gerente, new(Guid.NewGuid(), item, await db.Versao(mesa), "Esperou demais"), default);
        Assert.Equal("cortesia", await db.Escalar<string>("SELECT tipo FROM mesa_aviso_gestor WHERE id = @id", new { id = aviso.AvisoId }));
        Assert.Equal(12m, await db.Escalar<decimal>("SELECT valor FROM mesa_aviso_gestor WHERE id = @id", new { id = aviso.AvisoId }));

        // Cortesia até o limite da loja o garçom dá sozinho.
        await db.Config("""{ "autonomiaGarcom": { "limiteCortesia": 15 } }""");
        var chope2 = (await db.Lancar(db.Rafa, mesa, db.Chope)).ItemIds[0];
        await db.Pedidos.CortesiaAsync(db.Rafa, new(Guid.NewGuid(), chope2, await db.Versao(mesa), "Aniversário"), default);
        Assert.NotNull(await db.Escalar<string?>("SELECT cortesia::text FROM mesa_item WHERE id = @chope2", new { chope2 }, aceitaNulo: true));
    }

    [DeliveryDatabaseFact]
    public async Task Matriz_de_autonomia_no_comando()
    {
        await using var db = await Database.Create();
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var ids = (await db.Lancar(db.Rafa, mesa, db.Picanha, db.Bruschetta, db.Chope)).ItemIds;
        var (picanha, bruschetta, chope) = (ids[0], ids[1], ids[2]);

        // editar antes da cozinha: sozinho
        await db.Pedidos.EditarItemAsync(db.Rafa, new(Guid.NewGuid(), picanha, 2, "bem passada", null, null), default);
        Assert.Equal(2m, await db.Escalar<decimal>("SELECT qtd FROM mesa_item WHERE id = @picanha", new { picanha }));
        Assert.Equal(179.80m, await db.Escalar<decimal>("SELECT sum(valor) FROM mesa_item_parte WHERE item_id = @picanha", new { picanha }));

        // cancelar antes: com motivo
        Assert.Equal("EXIGE_MOTIVO", (await Erro(() => db.Pedidos.CancelarItemAsync(db.Rafa, new(Guid.NewGuid(), chope, null), default))).Codigo);
        await db.Pedidos.CancelarItemAsync(db.Rafa, new(Guid.NewGuid(), chope, "Cliente desistiu"), default);
        Assert.Equal("cancelado", await db.Estado(chope));

        // depois que a cozinha começou: editar ou cancelar pede o gerente, nada muda
        await db.Pedidos.AvancarEtapaAsync(db.Gerente, new(Guid.NewGuid(), bruschetta, null), default);
        Assert.Equal("AGUARDANDO_GERENTE", (await Erro(() => db.Pedidos.CancelarItemAsync(db.Rafa, new(Guid.NewGuid(), bruschetta, "errei"), default))).Codigo);
        Assert.Equal("AGUARDANDO_GERENTE", (await Erro(() => db.Pedidos.EditarItemAsync(db.Rafa, new(Guid.NewGuid(), bruschetta, 3, null, null, null), default))).Codigo);
        Assert.Equal("em_preparo", await db.Estado(bruschetta));
        await db.Pedidos.CancelarItemAsync(db.Gerente, new(Guid.NewGuid(), bruschetta, "errei"), default);
        Assert.Equal("cancelado", await db.Estado(bruschetta));

        // prato voltou: motivo sempre; volta para a fila como refazer
        await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default);
        Assert.Equal("EXIGE_MOTIVO", (await Erro(() => db.Pedidos.PratoVoltouAsync(db.Rafa, new(Guid.NewGuid(), picanha, null, null, null, null), default))).Codigo);
        await db.Pedidos.PratoVoltouAsync(db.Rafa, new(Guid.NewGuid(), picanha, null, null, null, "Frio"), default);
        Assert.Equal(("em_fila", 1), (await db.Estado(picanha), await db.Escalar<int>("SELECT refazer FROM mesa_item WHERE id = @picanha", new { picanha })));
        Assert.StartsWith("↻ Refazer: Frio", await db.Escalar<string>("SELECT obs FROM mesa_item WHERE id = @picanha", new { picanha }));

        // a loja muda a regra: cancelar antes passa a pedir o gerente
        await db.Config("""{ "autonomiaGarcom": { "regras": { "cancelarAntes": "gerente" } } }""");
        var novo = (await db.Lancar(db.Rafa, mesa, db.Chope)).ItemIds[0];
        Assert.Equal("AGUARDANDO_GERENTE", (await Erro(() => db.Pedidos.CancelarItemAsync(db.Rafa, new(Guid.NewGuid(), novo, "x"), default))).Codigo);

        // cozinha não lança nem entrega; garçom não marca pronto
        Assert.Equal("SEM_PERMISSAO", (await Erro(() => db.Lancar(db.CozinhaCtx, mesa, db.Chope))).Codigo);
        Assert.Equal("SEM_PERMISSAO", (await Erro(() => db.Pedidos.MarcarProntoAsync(db.Rafa, new(Guid.NewGuid(), novo, null), default))).Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task Setor_sem_painel_recebe_o_item_e_o_garcom_entrega_direto()
    {
        await using var db = await Database.Create();
        await db.Config("""{ "preparo": { "bar": { "existe": true, "painel": false, "via": "garcom" } } }""");
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var chope = (await db.Lancar(db.Rafa, mesa, db.Chope)).ItemIds[0];
        Assert.Equal("bar", await db.Escalar<string>("SELECT estacao_id FROM mesa_item WHERE id = @chope", new { chope }));
        var bar = await db.Cozinha.CozinhaAsync(db.Gerente, "bar", default);
        Assert.True(bar.Cartoes.Single().SemPainel);
        Assert.False(bar.Estacoes.Single(e => e.EstacaoId == "bar").Painel);

        await db.Pedidos.EntregarAsync(db.Rafa, new(Guid.NewGuid(), chope, null, null, null, null), default);
        Assert.True(await db.Escalar<bool>("SELECT entrega_direta AND NOT entrega_presumida FROM mesa_item WHERE id = @chope", new { chope }));

        // Bar que a loja não tem: cai na Produção geral.
        await db.Config("""{ "preparo": { "bar": { "existe": false } } }""");
        var outro = (await db.Lancar(db.Rafa, mesa, db.Chope)).ItemIds[0];
        Assert.Equal("producao_geral", await db.Escalar<string>("SELECT estacao_id FROM mesa_item WHERE id = @outro", new { outro }));
    }

    [DeliveryDatabaseFact]
    public async Task Entrega_presumida_ao_encerrar_e_depois_do_peguei()
    {
        await using var db = await Database.Create();
        var (m1, m2) = (db.Mesa("1"), db.Mesa("2"));
        await db.Abrir(m1);
        await db.Abrir(m2);

        // Depois do "Peguei": 20 min sem "Entreguei" fecha sozinho.
        var prato = (await db.Lancar(db.Rafa, m2, db.Picanha)).ItemIds[0];
        await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), prato, null), default);
        await db.Pedidos.PegarPratoAsync(db.Rafa, new(Guid.NewGuid(), prato, null, null, null, null), default);
        Assert.Equal(0, (await db.Sistema.TicarAsync(default)).Presumidos);
        await db.Execute("UPDATE mesa_item SET pegou_em = NOW() - INTERVAL '21 minutes' WHERE id = @prato", new { prato });
        Assert.Equal(1, (await db.Sistema.TicarAsync(default)).Presumidos);
        Assert.True(await db.Escalar<bool>("SELECT estado = 'entregue' AND entrega_presumida FROM mesa_item WHERE id = @prato", new { prato }));
        Assert.True(await db.Escalar<bool>("SELECT presumido FROM mesa_item_evento WHERE item_id = @prato AND para = 'entregue'", new { prato }));

        // Ao encerrar: o que estava na fila e o pronto fecham presumidos (na fila também marca entrega direta).
        var ids = (await db.Lancar(db.Rafa, m1, db.Picanha, db.Chope)).ItemIds;
        await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), ids[1], null), default);
        await db.Sessao.EncerrarAsync(db.Rafa, new(Guid.NewGuid(), m1, await db.Versao(m1), null, null), default);
        Assert.Equal(2, await db.Escalar<int>("SELECT count(*) FROM mesa_item WHERE id = ANY(@ids) AND estado = 'entregue' AND entrega_presumida", new { ids = ids.ToArray() }));
        Assert.True(await db.Escalar<bool>("SELECT entrega_direta FROM mesa_item WHERE id = @id", new { id = ids[0] }));
        Assert.False(await db.Escalar<bool>("SELECT entrega_direta FROM mesa_item WHERE id = @id", new { id = ids[1] }));

        // Loja que desligou "fechar sozinho": nada fecha.
        await db.Config("""{ "entrega": { "fechaSozinho": false } }""");
        var p2 = (await db.Lancar(db.Rafa, m2, db.Picanha)).ItemIds[0];
        await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), p2, null), default);
        await db.Pedidos.PegarPratoAsync(db.Rafa, new(Guid.NewGuid(), p2, null, null, null, null), default);
        await db.Execute("UPDATE mesa_item SET pegou_em = NOW() - INTERVAL '2 hours' WHERE id = @p2", new { p2 });
        await db.Sistema.TicarAsync(default);
        Assert.Equal("pronto", await db.Estado(p2));
    }

    [DeliveryDatabaseFact]
    public async Task Dois_garcons_pegando_o_mesmo_prato_so_um_leva()
    {
        await using var db = await Database.Create();
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var prato = (await db.Lancar(db.Rafa, mesa, db.Picanha)).ItemIds[0];
        await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), prato, null), default);

        var r = await Task.WhenAll(
            Tentar(() => db.Pedidos.PegarPratoAsync(db.Rafa, new(Guid.NewGuid(), prato, null, null, null, null), default)),
            Tentar(() => db.Pedidos.PegarPratoAsync(db.Lia, new(Guid.NewGuid(), prato, null, null, null, null), default)));
        Assert.Single(r, x => x is null);
        Assert.Single(r, x => x?.Codigo == "JA_FEITO");
        var quem = await db.Escalar<string>("SELECT pegou_por_nome FROM mesa_item WHERE id = @prato", new { prato });
        Assert.Contains(quem.Split(' ')[0], r.First(x => x is not null)!.Message);

        // Quem pegou entrega; o outro tentar entregar de novo recebe "já entregue por".
        var dono = quem.StartsWith("Rafa") ? db.Rafa : db.Lia;
        var outro = quem.StartsWith("Rafa") ? db.Lia : db.Rafa;
        await db.Pedidos.EntregarAsync(dono, new(Guid.NewGuid(), prato, null, null, null, null), default);
        var e = await Erro(() => db.Pedidos.EntregarAsync(outro, new(Guid.NewGuid(), prato, null, null, null, null), default));
        Assert.Equal("JA_FEITO", e.Codigo);
        Assert.Contains(quem.Split(' ')[0], e.Message);
    }

    [DeliveryDatabaseFact]
    public async Task Aprovacao_parada_escala_para_o_gerente_do_turno_uma_vez()
    {
        await using var db = await Database.Create();
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var ped = await db.Pedidos.LancarAsync(db.Gerente, new(Guid.NewGuid(), mesa,
            new[] { new ItemNovo(db.Picanha, 1, null, null, null, null, null, null) }, null, null, "cliente", null), default);
        Assert.Equal(0, (await db.Sistema.TicarAsync(default)).Escalados);           // ainda dentro dos 4 min
        await db.Execute("UPDATE mesa_pedido SET criado_em = NOW() - INTERVAL '5 minutes' WHERE id = @id", new { id = ped.PedidoId });
        Assert.Equal(1, (await db.Sistema.TicarAsync(default)).Escalados);
        Assert.Equal(db.MarinaPessoa, await db.Escalar<Guid>("SELECT escalado_para FROM mesa_pedido WHERE id = @id", new { id = ped.PedidoId }));
        Assert.Equal(0, (await db.Sistema.TicarAsync(default)).Escalados);
        Assert.Equal(1, await db.Escalar<int>("SELECT count(*) FROM mesa_evento WHERE acao = 'escalarAprovacao' AND ator_papel = 'sistema'"));

        // 0 desliga
        await db.Config("""{ "aprovacao": { "escalonarMin": 0 } }""");
        var p2 = await db.Pedidos.LancarAsync(db.Gerente, new(Guid.NewGuid(), mesa,
            new[] { new ItemNovo(db.Picanha, 1, null, null, null, null, null, null) }, null, null, "cliente", null), default);
        await db.Execute("UPDATE mesa_pedido SET criado_em = NOW() - INTERVAL '1 hour' WHERE id = @id", new { id = p2.PedidoId });
        Assert.Equal(0, (await db.Sistema.TicarAsync(default)).Escalados);
    }

    [DeliveryDatabaseFact]
    public async Task Voltar_o_ultimo_desfaz_passo_a_passo_e_cliente_busca_no_balcao()
    {
        await using var db = await Database.Create();
        await db.Config("""{ "retirada": { "modo": "depende", "estacoes": { "bar": "cliente" } } }""");
        var mesa = db.Mesa("1");
        await db.Abrir(mesa);
        var ids = (await db.Lancar(db.Rafa, mesa, db.Picanha, db.Chope)).ItemIds;
        var (picanha, chope) = (ids[0], ids[1]);

        await db.Pedidos.AvancarEtapaAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default);
        await db.Pedidos.MarcarItemProntoAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default);
        Assert.Equal("em_preparo", (await db.Pedidos.DesfazerPassoAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default)).Estado);
        Assert.Equal("em_fila", (await db.Pedidos.DesfazerPassoAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default)).Estado);
        Assert.Equal("ESTADO_INVALIDO", (await Erro(() => db.Pedidos.DesfazerPassoAsync(db.Gerente, new(Guid.NewGuid(), picanha, null), default))).Codigo);

        // Chope de retirada: aparece em "Cliente busca" e não é do garçom.
        Assert.Equal("aguardando_retirada", (await db.Pedidos.MarcarProntoAsync(db.Gerente, new(Guid.NewGuid(), chope, null), default)).Estado);
        var bar = await db.Cozinha.CozinhaAsync(db.Gerente, "bar", default);
        Assert.Equal("cliente", bar.Cartoes.Single().Faixa);
        Assert.Empty((await db.Cozinha.GarcomAsync(db.Rafa, null, default)).ParaLevar);
        await db.Pedidos.MarcarRetiradoAsync(db.CozinhaCtx, new(Guid.NewGuid(), chope, null), default);
        Assert.Equal("aguardando_retirada", (await db.Pedidos.DesfazerPassoAsync(db.Gerente, new(Guid.NewGuid(), chope, null), default)).Estado);
    }

    // ------------------------------------------------------------------ apoio

    private static async Task<MesasException> Erro(Func<Task> f) => await Tentar(f) ?? throw new Xunit.Sdk.XunitException("Esperava erro.");

    private static async Task<MesasException?> Tentar(Func<Task> f)
    {
        try { await f(); return null; }
        catch (MesasException e) { return e; }
    }

    private sealed class TempoRealFalso : IMesasTempoReal, IPlantaTempoReal, ICozinhaTempoReal
    {
        public Task MesaMudou(Guid loja, Guid mesaId, int versao) => Task.CompletedTask;
        public Task SalaoMudou(Guid loja, int versaoPlanta) => Task.CompletedTask;
        public Task PendenciasMudou(Guid loja, string tipo, Guid? garcomId) => Task.CompletedTask;
        public Task Aviso(Guid loja, string tipo, Guid id, Guid? mesaId, Guid? garcomId) => Task.CompletedTask;
        public Task SolicitacaoMudou(Guid loja, Guid id, string estado) => Task.CompletedTask;
        public Task CozinhaMudou(Guid loja, IEnumerable<string> estacaoIds, Guid? pedidoId) => Task.CompletedTask;
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public SessaoService Sessao { get; private set; } = null!;
        public PedidosService Pedidos { get; private set; } = null!;
        public CozinhaService Cozinha { get; private set; } = null!;
        public PedidosSistema Sistema { get; private set; } = null!;
        public Guid A { get; } = Guid.NewGuid();
        public Guid RafaPessoa { get; } = Guid.NewGuid();
        public Guid LiaPessoa { get; } = Guid.NewGuid();
        public Guid MarinaPessoa { get; } = Guid.NewGuid();
        public Guid Picanha { get; } = Guid.NewGuid();
        public Guid Bruschetta { get; } = Guid.NewGuid();
        public Guid Chope { get; } = Guid.NewGuid();
        public MesasContexto Gerente => new(A, 10, MesasPermissoes.PapelGerente);
        public MesasContexto Rafa => new(A, 30, MesasPermissoes.PapelGarcom);
        public MesasContexto Lia => new(A, 40, MesasPermissoes.PapelGarcom);
        public MesasContexto CozinhaCtx => new(A, 50, MesasPermissoes.PapelCozinha);
        private readonly Dictionary<string, Guid> _mesas = new();
        private readonly string _schema = "mesas_pedidos_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public Guid Mesa(string numero) => _mesas[numero];

        public static async Task<Database> Create()
        {
            DefaultTypeMap.MatchNamesWithUnderscores = true;
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            var tempo = new TempoRealFalso();
            var comandos = new MesasComandos(db.Source, tempo);
            db.Sessao = new SessaoService(comandos, new IMesasSessaoGancho[] { new PedidosSessaoGancho(tempo) });
            db.Pedidos = new PedidosService(db.Source, comandos, tempo);
            db.Cozinha = new CozinhaService(db.Source);
            db.Sistema = new PedidosSistema(db.Source, tempo, tempo);
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
                CREATE TABLE cardapio_categoria (id uuid PRIMARY KEY, id_estabelecimento uuid NOT NULL, nome text NOT NULL, slug text NOT NULL);
                CREATE TABLE cardapio_produto (id uuid PRIMARY KEY, id_estabelecimento uuid NOT NULL, categoria_id uuid NOT NULL REFERENCES cardapio_categoria (id),
                  nome text NOT NULL, preco_base numeric(10,2) NOT NULL, imagem_url text, ativo boolean NOT NULL DEFAULT true,
                  disponivel boolean NOT NULL DEFAULT true, deleted_at timestamptz);
                CREATE FUNCTION gestao_bloqueia_alteracao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'imutavel'; END $$;
                CREATE FUNCTION gestao_bloqueia_exclusao() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'sem exclusao'; END $$;
                INSERT INTO estabelecimentos VALUES (@A, 'Loja A', ARRAY['MESAS']);
                INSERT INTO usuario VALUES (10, 'Ana Gerente'), (30, 'Rafa Lima'), (40, 'Lia Souza'), (50, 'Cozinha'), (60, 'Marina Alves');
                """, new { db.A });
            foreach (var arquivo in new[] { "20261203_03_mesas_nucleo.sql", "20261205_05_mesas_planta.sql", "20261206_01_mesas_sessao.sql",
                         "20261207_01_mesas_pedidos.sql", "20261207_01_mesas_pedidos.sql" })
                await db.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", arquivo)));
            var (pratos, entradas, bebidas) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await db.Execute("""
                INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, vinculo, usuario_id, funcao_codigo) VALUES
                  (@rafa, @A, 'Rafa Lima', 'clt', 30, 'garcom'), (@lia, @A, 'Lia Souza', 'clt', 40, 'garcom'),
                  (@marina, @A, 'Marina Alves', 'clt', 60, 'gerente_salao');
                INSERT INTO gestao_ponto VALUES (gen_random_uuid(), @A, @rafa, NOW() - INTERVAL '1 hour', NULL), (gen_random_uuid(), @A, @lia, NOW() - INTERVAL '1 hour', NULL),
                  (gen_random_uuid(), @A, @marina, NOW() - INTERVAL '1 hour', NULL);
                INSERT INTO cardapio_categoria VALUES (@pratos, @A, 'Pratos principais', 'pratos-principais'), (@entradas, @A, 'Entradas', 'entradas'),
                  (@bebidas, @A, 'Bebidas', 'bebidas');
                INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, preco_base) VALUES
                  (@picanha, @A, @pratos, 'Picanha', 89.90), (@bruschetta, @A, @entradas, 'Bruschetta', 32), (@chope, @A, @bebidas, 'Chope', 12);
                """, new { db.A, rafa = db.RafaPessoa, lia = db.LiaPessoa, marina = db.MarinaPessoa, pratos, entradas, bebidas, picanha = db.Picanha,
                bruschetta = db.Bruschetta, chope = db.Chope });
            await db.Execute($$"""
                INSERT INTO mesas_config (estabelecimento_id, ativo, dados) VALUES (@A, true,
                  '{ "rotas": { "categorias": { "{{pratos}}": "cozinha", "{{entradas}}": "cozinha", "{{bebidas}}": "bar" } } }'::jsonb)
                """, new { db.A });

            var planta = new PlantaService(db.Source, tempo);
            var salvo = await planta.SalvarRascunhoAsync(db.Gerente, new(null, PlantaRegrasTests.Planta(), null), default);
            await planta.PublicarAsync(db.Gerente, new(null, salvo.VersaoRascunho), default);
            await using var c = await db.Source.OpenConnectionAsync();
            foreach (var (numero, id) in await c.QueryAsync<(string, Guid)>("SELECT numero, id FROM mesa")) db._mesas[numero] = id;
            return db;
        }

        /// <summary>Mescla um pedaço na configuração da loja (objetos se juntam, como no PUT /config).</summary>
        public async Task Config(string json)
        {
            await EnsureMerge();
            await Execute("UPDATE mesas_config SET dados = mesas_merge(dados, CAST(@json AS jsonb)) WHERE estabelecimento_id = @A", new { json, A });
        }

        public async Task Abrir(Guid mesa) =>
            await Sessao.AbrirMesaAsync(Rafa, new(Guid.NewGuid(), mesa, await Versao(mesa), 2, RafaPessoa, null, null, null, null), default);

        public Task<PedidoLancadoDto> Lancar(MesasContexto ctx, Guid mesa, params Guid[] produtos) =>
            Pedidos.LancarAsync(ctx, new(Guid.NewGuid(), mesa, produtos.Select(p => new ItemNovo(p, 1, null, null, null, null, null, null)).ToList(), null, null, null, null), default);

        public Task<int> Versao(Guid mesa) => Escalar<int>("SELECT versao FROM mesa_estado WHERE mesa_id = @mesa", new { mesa });
        public Task<string> Estado(Guid item) => Escalar<string>("SELECT estado FROM mesa_item WHERE id = @item", new { item });

        private bool _merge;
        private async Task EnsureMerge()
        {
            if (_merge) return;
            _merge = true;
            await Execute("""
                CREATE OR REPLACE FUNCTION mesas_merge(a jsonb, b jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE AS $$
                  SELECT jsonb_object_agg(k, CASE WHEN jsonb_typeof(a -> k) = 'object' AND jsonb_typeof(b -> k) = 'object'
                                                  THEN mesas_merge(a -> k, b -> k) ELSE COALESCE(b -> k, a -> k) END)
                    FROM (SELECT jsonb_object_keys(a) AS k UNION SELECT jsonb_object_keys(b)) x
                $$;
                """);
        }

        public async Task<T> Escalar<T>(string sql, object? args = null, bool aceitaNulo = false)
        {
            await using var cx = await Source.OpenConnectionAsync();
            var v = await cx.ExecuteScalarAsync<T>(sql, args);
            if (v is null && !aceitaNulo) throw new InvalidOperationException("Sem resultado: " + sql);
            return v!;
        }

        public async Task<List<T>> Lista<T>(string sql, object? args = null)
        {
            await using var cx = await Source.OpenConnectionAsync();
            return (await cx.QueryAsync<T>(sql, args)).ToList();
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
