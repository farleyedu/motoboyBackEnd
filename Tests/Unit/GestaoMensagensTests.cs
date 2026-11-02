using APIBack.GestaoMensagens;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// Mensagens da Gestao por tres canais: janela de 24 h por numero e contato, modelo do mesmo numero, 422 sem modelo, envio pessoal sem fingir
/// entrega, recibos sem regressao, marcos (aguardando loja / esperando resposta) so com envio ou recebimento reais, e isolamento entre numeros.
/// Store, Meta, painel e marcos sao fakes em memoria.
/// </summary>
public class GestaoMensagensTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Loja = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lead = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CanalA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CanalB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid CanalPessoalAna = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private const int Ana = 7, Beto = 8;
    private const string Fone = "+5534999990001";

    private sealed class Relogio : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private sealed class Store : IGestaoMensagensStore
    {
        public List<CanalGestao> Canais = new();
        public List<ContatoAlvo> ContatosLoja = new() { new("10", "Dono", Fone, true) };
        public Dictionary<Guid, List<ModeloAprovado>> Modelos = new();
        public HashSet<string> OptOuts = new();
        public List<MensagemRegistro> Mensagens = new();

        public Task<AlvoMensagem?> ObterAlvoAsync(Guid? l, Guid? o, CancellationToken ct) => Task.FromResult<AlvoMensagem?>(
            l == Loja ? new AlvoMensagem("loja", Loja, null, "Loja X") : o == Lead ? new AlvoMensagem("oportunidade", null, Lead, "Lead Y") : null);
        public Task<IReadOnlyList<ContatoAlvo>> ContatosAsync(AlvoMensagem a, CancellationToken ct) => Task.FromResult<IReadOnlyList<ContatoAlvo>>(
            a.Tipo == "loja" ? ContatosLoja : new[] { new ContatoAlvo("principal", "Lead Y", Fone, true) });
        public Task<IReadOnlyList<CanalGestao>> CanaisAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CanalGestao>>(Canais);
        public Task<DateTimeOffset?> UltimaEntradaAsync(Guid canal, string chave, CancellationToken ct) => Task.FromResult(
            Mensagens.Where(m => m.Direcao == DirecaoMensagem.Entrada && m.CanalId == canal && m.TelefoneChave == chave).Select(m => (DateTimeOffset?)m.CriadoEm).DefaultIfEmpty().Max());
        public Task<IReadOnlyList<ModeloAprovado>> ModelosAprovadosAsync(Guid canal, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModeloAprovado>>(Modelos.TryGetValue(canal, out var l) ? l : new List<ModeloAprovado>());
        public Task<bool> OptOutAsync(string chave, CancellationToken ct) => Task.FromResult(OptOuts.Contains(chave));
        public Task<MensagemRegistro?> PorChaveAsync(int autor, string chave, CancellationToken ct) =>
            Task.FromResult(Mensagens.FirstOrDefault(m => m.AutorUsuarioId == autor && m.ChaveIdempotencia == chave && m.Direcao == DirecaoMensagem.Saida));
        public Task<bool> InserirAsync(MensagemRegistro m, CancellationToken ct)
        {
            if (m.ChaveIdempotencia != null && Mensagens.Any(x => x.AutorUsuarioId == m.AutorUsuarioId && x.ChaveIdempotencia == m.ChaveIdempotencia)) return Task.FromResult(false);
            if (m.ReferenciaExterna != null && m.CanalId != null && Mensagens.Any(x => x.CanalId == m.CanalId && x.Direcao == m.Direcao && x.ReferenciaExterna == m.ReferenciaExterna)) return Task.FromResult(false);
            Mensagens.Add(m);
            return Task.FromResult(true);
        }
        public Task<MensagemRegistro?> ObterAsync(Guid id, CancellationToken ct) => Task.FromResult(Mensagens.FirstOrDefault(m => m.Id == id));
        public Task<bool> AtualizarAsync(MensagemRegistro m, string? esperado, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<MensagemRegistro>> HistoricoAsync(Guid? l, Guid? o, int limite, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MensagemRegistro>>(Mensagens.Where(m => (l != null && m.EstabelecimentoId == l) || (o != null && m.OportunidadeId == o)).ToList());
        public Task<bool> CanalDaGestaoAsync(Guid canal, CancellationToken ct) => Task.FromResult(Canais.Any(c => c.CanalId == canal));
        public Task<MensagemRegistro?> UltimaSaidaAsync(Guid canal, string chave, CancellationToken ct) => Task.FromResult(
            Mensagens.Where(m => m.Direcao == DirecaoMensagem.Saida && m.CanalId == canal && m.TelefoneChave == chave
                                 && m.Estado is EstadoMensagem.Enviado or EstadoMensagem.Entregue or EstadoMensagem.Lido).OrderByDescending(m => m.CriadoEm).FirstOrDefault());
        public Task<MensagemRegistro?> PorReferenciaSaidaAsync(string r, CancellationToken ct) =>
            Task.FromResult(Mensagens.FirstOrDefault(m => m.ReferenciaExterna == r && m.Direcao == DirecaoMensagem.Saida));
    }

    private sealed class Meta : IGestaoEnviadorMeta
    {
        public List<(Guid Canal, string Tipo, string Conteudo)> Enviados = new();
        public bool Falhar, SemId;
        private int _n;
        public Task<string?> EnviarTextoAsync(CanalGestao c, string d, string t, CancellationToken ct) => Enviar(c, "texto", t);
        public Task<string?> EnviarModeloAsync(CanalGestao c, string d, ModeloAprovado m, IReadOnlyList<string> corpo, string? s, CancellationToken ct) => Enviar(c, "modelo", m.Nome + ":" + string.Join("|", corpo));
        private Task<string?> Enviar(CanalGestao c, string tipo, string conteudo)
        {
            if (Falhar) throw new HttpRequestException("Meta fora do ar");
            if (SemId) return Task.FromResult<string?>(null);
            Enviados.Add((c.CanalId, tipo, conteudo));
            return Task.FromResult<string?>($"wamid.{++_n}");
        }
    }

    private sealed class Painel : IGestaoPainel
    {
        public List<(Guid Loja, string Texto, long? Chamado)> Chamados = new();
        public Task<long> EnviarAsync(Guid loja, int autor, string texto, long? chamado, CancellationToken ct) { Chamados.Add((loja, texto, chamado)); return Task.FromResult(chamado ?? 99L); }
    }

    private sealed class Marcos : IGestaoMarcos
    {
        public List<(Guid Ref, string Direcao, string Mensagem)> Lista = new();
        public bool Quebrar;
        public Task RegistrarAsync(Guid r, string d, DateTimeOffset em, string m, CancellationToken ct)
        {
            if (Quebrar) throw new InvalidOperationException("banco fora");
            if (!Lista.Any(x => x.Ref == r && x.Direcao == d && x.Mensagem == m)) Lista.Add((r, d, m));
            return Task.CompletedTask;
        }
    }

    private sealed class Cenario
    {
        public Relogio Clock = new();
        public Store Store = new();
        public Meta Meta = new();
        public Painel Painel = new();
        public Marcos Marcos = new();
        public GestaoMensagensService Service;
        public GestaoMensagensRetorno Retorno;
        public AtorGestao AtorAna = new(Ana, "Ana", true);
        public AtorGestao AtorBeto = new(Beto, "Beto", true);

        public Cenario()
        {
            Store.Canais.Add(new CanalGestao(Guid.NewGuid(), CanalA, "pidA", "+553430000001", "Comercial", "comercial", null, true, true, "ativo", new[] { Ana, Beto }));
            Store.Canais.Add(new CanalGestao(Guid.NewGuid(), CanalB, "pidB", "+553430000002", "Suporte", "suporte", null, true, true, "ativo", new[] { Ana }));
            Service = new GestaoMensagensService(Store, Meta, Painel, Marcos, Clock, NullLogger<GestaoMensagensService>.Instance);
            Retorno = new GestaoMensagensRetorno(Store, Marcos, NullLogger<GestaoMensagensRetorno>.Instance);
        }

        public Task<MensagemDto> Enviar(AtorGestao ator, string canal, string? texto = "Ola", ModeloEnvio? modelo = null, string? chave = null, Guid? loja = null, Guid? lead = null,
            string? finalidade = null, bool consent = false, string? contato = null) =>
            Service.EnviarAsync(ator, new EnviarMensagemRequest(lead == null ? loja ?? Loja : null, lead, contato, canal, modelo == null ? texto : null, modelo,
                chave ?? Guid.NewGuid().ToString("N"), finalidade, consent, null), default);

        public Task Recebida(Guid canal, string wamid, string remetente = "5534999990001") => Retorno.RegistrarRecebidaAsync(canal, remetente, wamid, Clock.Agora, "oi");
        public ModeloAprovado Modelo(Guid canal, string nome = "aviso_incidente", string categoria = "UTILITY", int vars = 2)
        {
            var m = new ModeloAprovado(Guid.NewGuid(), nome, "pt_BR", categoria, "Ola {{1}}, sobre {{2}}", vars, false);
            if (!Store.Modelos.TryGetValue(canal, out var l)) Store.Modelos[canal] = l = new();
            l.Add(m);
            return m;
        }
    }

    private static async Task<GestaoMensagensException> Falha(Func<Task> acao)
    {
        try { await acao(); }
        catch (GestaoMensagensException ex) { return ex; }
        throw new Xunit.Sdk.XunitException("Esperava GestaoMensagensException.");
    }

    // ------------------------------------------------------------------------------------- janela por numero

    [Fact]
    public async Task Mesmo_contato_recebe_texto_no_numero_A_e_exige_modelo_no_B()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");           // o contato escreveu para o numero A, nao para o B
        c.Modelo(CanalB);

        var a = await c.Enviar(c.AtorAna, $"numero:{CanalA}", "Texto livre");
        Assert.Equal(EstadoMensagem.Enviado, a.Estado);
        Assert.Equal("texto", c.Meta.Enviados.Single().Tipo);

        var b = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalB}", "Texto livre"));
        Assert.Equal(422, b.Status);
        Assert.Equal(CodigosGestaoMensagem.JanelaFechadaSemModelo, b.Codigo);
        Assert.Contains("aviso_incidente", b.ProximoPasso);
        Assert.Single(c.Meta.Enviados);                   // nada saiu pelo B

        var modelo = c.Store.Modelos[CanalB][0];
        var ok = await c.Enviar(c.AtorAna, $"numero:{CanalB}", modelo: new ModeloEnvio(modelo.Id, null, new[] { "Dono", "falha de ontem" }, null));
        Assert.Equal(EstadoMensagem.Enviado, ok.Estado);
        Assert.Equal(("modelo"), c.Meta.Enviados[1].Tipo);
        Assert.Equal(CanalB, c.Meta.Enviados[1].Canal);
    }

    [Fact]
    public async Task Janela_fechada_sem_modelo_no_numero_da_422_com_alternativas_e_nao_envia_nem_marca()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");           // A tem janela aberta: e a alternativa
        var ex = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalB}"));
        Assert.Equal(422, ex.Status);
        Assert.Equal(CodigosGestaoMensagem.JanelaFechadaSemModelo, ex.Codigo);
        Assert.Contains("crie um", ex.ProximoPasso);
        Assert.Contains("outro numero", ex.ProximoPasso);
        Assert.Empty(c.Meta.Enviados);
        Assert.Empty(c.Marcos.Lista);
        Assert.DoesNotContain(c.Store.Mensagens, m => m.Direcao == DirecaoMensagem.Saida);
    }

    [Fact]
    public async Task Janela_expira_24h_depois_da_ultima_mensagem_recebida()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        c.Clock.Agora = T0.AddHours(23);
        Assert.Equal(EstadoMensagem.Enviado, (await c.Enviar(c.AtorAna, $"numero:{CanalA}")).Estado);
        c.Clock.Agora = T0.AddHours(25);
        Assert.Equal(CodigosGestaoMensagem.JanelaFechadaSemModelo, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"))).Codigo);
    }

    [Fact]
    public async Task Opcoes_mostram_janela_por_numero_modelos_e_motivo_quando_nao_da_para_enviar()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        var opcoes = await c.Service.OpcoesAsync(c.AtorAna, Loja, null, null, default);
        var a = opcoes.Canais.Single(o => o.Canal == $"numero:{CanalA}");
        var b = opcoes.Canais.Single(o => o.Canal == $"numero:{CanalB}");
        Assert.True(a.Disponivel);
        Assert.True(a.JanelaAberta);
        Assert.Equal(T0.AddHours(24), a.JanelaAteUtc);
        Assert.False(b.Disponivel);
        Assert.False(b.JanelaAberta);
        Assert.NotNull(b.Motivo);
        Assert.NotNull(b.ProximoPasso);
        Assert.Contains($"numero:{CanalA}", b.Alternativas);
        Assert.Contains(opcoes.Canais, o => o.Canal == CanaisMensagem.Painel && o.Disponivel);
        Assert.Contains(opcoes.Canais, o => o.Canal == CanaisMensagem.Pessoal && o.Registro == "manual");

        var beto = await c.Service.OpcoesAsync(c.AtorBeto, Loja, null, null, default);
        Assert.Contains("nao esta na lista", beto.Canais.Single(o => o.Canal == $"numero:{CanalB}").Motivo, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------- modelos e variaveis

    [Fact]
    public async Task Modelo_de_outro_numero_nao_serve_e_variaveis_sao_validadas()
    {
        var c = new Cenario();
        var deA = c.Modelo(CanalA);
        var ex = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalB}", modelo: new ModeloEnvio(deA.Id, null, new[] { "x", "y" }, null)));
        Assert.Equal(CodigosGestaoMensagem.ModeloNaoAprovado, ex.Codigo);
        Assert.Equal(422, ex.Status);

        Assert.Equal(CodigosGestaoMensagem.VariaveisInvalidas,
            (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", modelo: new ModeloEnvio(deA.Id, null, new[] { "so uma" }, null)))).Codigo);
        Assert.Equal(CodigosGestaoMensagem.VariaveisInvalidas,
            (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", modelo: new ModeloEnvio(deA.Id, null, new[] { "linha1\nlinha2", "y" }, null)))).Codigo);
        Assert.Empty(c.Meta.Enviados);
    }

    [Fact]
    public async Task Marketing_exige_modelo_consentimento_e_ausencia_de_opt_out()
    {
        var c = new Cenario();
        var m = c.Modelo(CanalA, "promo", "MARKETING", 1);
        var modelo = new ModeloEnvio(m.Id, null, new[] { "Dono" }, null);

        Assert.Equal(CodigosGestaoMensagem.ConsentimentoObrigatorio, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", modelo: modelo))).Codigo);
        c.Store.OptOuts.Add("553499990001");
        Assert.Equal(CodigosGestaoMensagem.OptOut, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", modelo: modelo, consent: true))).Codigo);
        c.Store.OptOuts.Clear();
        var ok = await c.Enviar(c.AtorAna, $"numero:{CanalA}", modelo: modelo, consent: true);
        Assert.Equal(FinalidadeMensagem.Marketing, ok.Finalidade);
        Assert.Equal(CodigosGestaoMensagem.ModeloNaoAprovado,
            (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", "texto livre", finalidade: "marketing", consent: true))).Codigo);
    }

    // ------------------------------------------------------------------------------------- permissao e isolamento

    [Fact]
    public async Task Permissao_de_remetente_por_numero_e_somente_equipe_da_zippy()
    {
        var c = new Cenario();
        await c.Recebida(CanalB, "wamid.in1");
        var ex = await Falha(() => c.Enviar(c.AtorBeto, $"numero:{CanalB}"));   // Beto nao esta na lista do B
        Assert.Equal(403, ex.Status);
        Assert.Equal(CodigosGestaoMensagem.SemPermissaoNoCanal, ex.Codigo);
        Assert.Equal(403, (await Falha(() => c.Enviar(new AtorGestao(99, "Loja", false), $"numero:{CanalA}"))).Status);
        Assert.Equal(404, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{Guid.NewGuid()}"))).Status);
        Assert.Equal(422, (await Falha(() => c.Enviar(c.AtorAna, "sms"))).Status);
        Assert.Empty(c.Meta.Enviados);
    }

    [Fact]
    public async Task Numero_pessoal_de_outra_pessoa_nao_aparece_nem_e_usado()
    {
        var c = new Cenario();
        c.Store.Canais.Add(new CanalGestao(Guid.NewGuid(), CanalPessoalAna, "pidP", "+553499990009", "Ana", "pessoal", Ana, true, true, "ativo", Array.Empty<int>()));
        Assert.DoesNotContain(await c.Service.CanaisAsync(c.AtorBeto, default), x => x.CanalId == CanalPessoalAna);
        Assert.Contains(await c.Service.CanaisAsync(c.AtorAna, default), x => x.CanalId == CanalPessoalAna);
        Assert.Equal(404, (await Falha(() => c.Enviar(c.AtorBeto, $"numero:{CanalPessoalAna}"))).Status);
    }

    [Fact]
    public async Task Numero_desconectado_na_meta_da_409_com_proximo_passo()
    {
        var c = new Cenario();
        c.Store.Canais[0] = c.Store.Canais[0] with { ConectadoMeta = false, EstadoMeta = "erro: token invalido" };
        var ex = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"));
        Assert.Equal(409, ex.Status);
        Assert.Equal(CodigosGestaoMensagem.CanalIndisponivel, ex.Codigo);
        Assert.NotNull(ex.ProximoPasso);
    }

    [Fact]
    public async Task Destino_inexistente_404_contato_ambiguo_409_e_telefone_nao_confirmado_422()
    {
        var c = new Cenario();
        Assert.Equal(404, (await Falha(() => c.Enviar(c.AtorAna, "painel", loja: Guid.NewGuid()))).Status);

        c.Store.ContatosLoja = new() { new("10", "Dono", Fone, true), new("11", "Gerente", "+5534999990002", true) };
        var amb = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"));
        Assert.Equal(409, amb.Status);
        Assert.Equal(CodigosGestaoMensagem.ContatoAmbiguo, amb.Codigo);
        Assert.Equal(404, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}", contato: "999"))).Status);

        c.Store.ContatosLoja = new() { new("10", "Dono", null, false) };
        Assert.Equal(CodigosGestaoMensagem.ContatoNaoConfirmado, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"))).Codigo);
    }

    // ------------------------------------------------------------------------------------- painel

    [Fact]
    public async Task Painel_so_para_loja_usa_chamado_e_registra_o_envio_real()
    {
        var c = new Cenario();
        var r = await c.Enviar(c.AtorAna, "painel", "Aviso para a loja");
        Assert.Equal(EstadoMensagem.Enviado, r.Estado);
        Assert.Equal(99, r.ChamadoId);
        Assert.Equal("Aviso para a loja", c.Painel.Chamados.Single().Texto);
        Assert.Contains(c.Marcos.Lista, m => m.Ref == Loja && m.Direcao == "enviada");

        var lead = await Falha(() => c.Enviar(c.AtorAna, "painel", lead: Lead));
        Assert.Equal(422, lead.Status);
        Assert.Equal(CodigosGestaoMensagem.CanalInvalido, lead.Codigo);
        Assert.Single(c.Painel.Chamados);
    }

    // ------------------------------------------------------------------------------------- pessoal

    [Fact]
    public async Task Pessoal_sem_meta_devolve_link_e_nao_finge_entrega_nem_marca_aguardando_loja()
    {
        var c = new Cenario();
        var r = await c.Enviar(c.AtorAna, "pessoal", "Oi, tudo bem?");
        Assert.Equal(EstadoMensagem.AguardandoConfirmacao, r.Estado);
        Assert.StartsWith("https://wa.me/5534999990001?text=", r.LinkWhatsapp);
        Assert.Empty(c.Meta.Enviados);
        Assert.Empty(c.Marcos.Lista);                                  // ninguem enviou ainda

        var confirmada = await c.Service.ConfirmarEnvioExternoAsync(c.AtorAna, r.Id, default);
        Assert.Equal(EstadoMensagem.EnviadoPorFora, confirmada.Estado);
        Assert.Null(confirmada.ReferenciaExterna);                     // sem wamid: nao e entrega da Meta
        Assert.Contains("Sem recibo", confirmada.Aviso);
        Assert.Empty(c.Meta.Enviados);
        Assert.Equal(("enviada", $"externo:{r.Id:N}"), (c.Marcos.Lista.Single().Direcao, c.Marcos.Lista.Single().Mensagem));
        Assert.True((await c.Service.ConfirmarEnvioExternoAsync(c.AtorAna, r.Id, default)).Repetida);
        Assert.Single(c.Marcos.Lista);
    }

    [Fact]
    public async Task Confirmacao_externa_so_do_autor_e_so_para_envio_pessoal()
    {
        var c = new Cenario();
        var r = await c.Enviar(c.AtorAna, "pessoal");
        Assert.Equal(403, (await Falha(() => c.Service.ConfirmarEnvioExternoAsync(c.AtorBeto, r.Id, default))).Status);
        Assert.Equal(404, (await Falha(() => c.Service.ConfirmarEnvioExternoAsync(c.AtorAna, Guid.NewGuid(), default))).Status);
        var painel = await c.Enviar(c.AtorAna, "painel");
        Assert.Equal(409, (await Falha(() => c.Service.ConfirmarEnvioExternoAsync(c.AtorAna, painel.Id, default))).Status);
        Assert.Empty(c.Marcos.Lista.Where(m => m.Mensagem.StartsWith("externo:")));
    }

    [Fact]
    public async Task Pessoal_com_numero_meta_conectado_funciona_como_numero_com_registro_completo()
    {
        var c = new Cenario();
        c.Store.Canais.Add(new CanalGestao(Guid.NewGuid(), CanalPessoalAna, "pidP", "+553499990009", "Ana", "pessoal", Ana, true, true, "ativo", Array.Empty<int>()));
        await c.Recebida(CanalPessoalAna, "wamid.in1");
        var r = await c.Enviar(c.AtorAna, "pessoal", "Oi");
        Assert.Equal(EstadoMensagem.Enviado, r.Estado);
        Assert.Equal(CanalPessoalAna, c.Meta.Enviados.Single().Canal);
        Assert.StartsWith("wamid.", r.ReferenciaExterna);

        // Sem janela no numero pessoal, tambem exige modelo (mesma regra dos outros numeros).
        c.Clock.Agora = T0.AddDays(3);
        Assert.Equal(CodigosGestaoMensagem.JanelaFechadaSemModelo, (await Falha(() => c.Enviar(c.AtorAna, "pessoal"))).Codigo);
    }

    // ------------------------------------------------------------------------------------- estados e marcos

    [Fact]
    public async Task Envio_real_marca_aguardando_loja_mas_falha_na_meta_nao_marca()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        c.Marcos.Lista.Clear();
        c.Meta.Falhar = true;
        var ex = await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"));
        Assert.Equal(502, ex.Status);
        Assert.Equal(CodigosGestaoMensagem.FalhaNaMeta, ex.Codigo);
        Assert.Empty(c.Marcos.Lista);
        var falhou = c.Store.Mensagens.Single(m => m.Direcao == DirecaoMensagem.Saida);
        Assert.Equal(EstadoMensagem.Falhou, falhou.Estado);
        Assert.NotNull(falhou.ErroCodigo);

        c.Meta.Falhar = false;
        c.Meta.SemId = true;                                           // Meta nao devolveu wamid: nao confirma
        Assert.Equal(502, (await Falha(() => c.Enviar(c.AtorAna, $"numero:{CanalA}"))).Status);
        Assert.Empty(c.Marcos.Lista);

        c.Meta.SemId = false;
        var ok = await c.Enviar(c.AtorAna, $"numero:{CanalA}");
        Assert.Equal(("enviada", ok.ReferenciaExterna), (c.Marcos.Lista.Single().Direcao, c.Marcos.Lista.Single().Mensagem));
        Assert.Equal(Loja, c.Marcos.Lista.Single().Ref);
    }

    [Fact]
    public async Task Marco_que_falha_nao_desfaz_o_envio_e_avisa()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        c.Marcos.Quebrar = true;
        var r = await c.Enviar(c.AtorAna, $"numero:{CanalA}");
        Assert.Equal(EstadoMensagem.Enviado, r.Estado);
        Assert.NotNull(r.Aviso);
    }

    [Fact]
    public async Task Recibos_atualizam_estado_sem_regredir_e_nunca_geram_marco_de_resposta()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        var r = await c.Enviar(c.AtorAna, $"numero:{CanalA}");
        var marcosAntes = c.Marcos.Lista.Count;

        Assert.True(await c.Retorno.AplicarReciboAsync(r.ReferenciaExterna!, "read", null, null));
        Assert.Equal(EstadoMensagem.Lido, c.Store.Mensagens.Single(m => m.Id == r.Id).Estado);
        Assert.True(await c.Retorno.AplicarReciboAsync(r.ReferenciaExterna!, "delivered", null, null));   // atrasado: nao regride
        Assert.True(await c.Retorno.AplicarReciboAsync(r.ReferenciaExterna!, "failed", "131047", "Re-engagement"));  // depois de lido: ignorado
        Assert.Equal(EstadoMensagem.Lido, c.Store.Mensagens.Single(m => m.Id == r.Id).Estado);
        Assert.False(await c.Retorno.AplicarReciboAsync("wamid.desconhecido", "read", null, null));        // nao e da Gestao
        Assert.Equal(marcosAntes, c.Marcos.Lista.Count);
        Assert.DoesNotContain(c.Marcos.Lista, m => m.Direcao == "recebida" && m.Ref == Loja);
    }

    [Fact]
    public async Task Falha_depois_de_aceita_pela_meta_mostra_a_causa()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        var r = await c.Enviar(c.AtorAna, $"numero:{CanalA}");
        await c.Retorno.AplicarReciboAsync(r.ReferenciaExterna!, "failed", "131026", "Numero sem WhatsApp");
        var m = c.Store.Mensagens.Single(x => x.Id == r.Id);
        Assert.Equal(EstadoMensagem.Falhou, m.Estado);
        Assert.Equal("131026", m.ErroCodigo);
        Assert.Equal("Numero sem WhatsApp", m.ErroDetalhe);
    }

    [Fact]
    public async Task Retorno_entra_no_historico_do_mesmo_numero_e_so_ele_gera_esperando_resposta()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in0");                         // abre a janela do A
        var r = await c.Enviar(c.AtorAna, $"numero:{CanalA}");
        Assert.DoesNotContain(c.Marcos.Lista, m => m.Direcao == "recebida" && m.Ref == Loja);

        c.Clock.Agora = T0.AddMinutes(30);
        await c.Recebida(CanalA, "wamid.in1", "553499990001");
        var entrada = c.Store.Mensagens.Single(m => m.ReferenciaExterna == "wamid.in1");
        Assert.Equal(DirecaoMensagem.Entrada, entrada.Direcao);
        Assert.Equal(CanalA, entrada.CanalId);
        Assert.Equal(Loja, entrada.EstabelecimentoId);                 // mesma loja da ultima saida deste numero
        Assert.Contains(c.Marcos.Lista, m => m.Direcao == "recebida" && m.Ref == Loja && m.Mensagem == "wamid.in1");

        await c.Recebida(CanalA, "wamid.in1", "553499990001");         // webhook reentregue
        Assert.Single(c.Marcos.Lista, m => m.Direcao == "recebida" && m.Mensagem == "wamid.in1");
        Assert.Single(c.Store.Mensagens, m => m.ReferenciaExterna == "wamid.in1");

        // Resposta no numero B (sem saida nele) nao e atribuida a loja pelo numero A e nao abre a janela do A.
        await c.Recebida(CanalB, "wamid.inB");
        var deB = c.Store.Mensagens.Single(m => m.ReferenciaExterna == "wamid.inB");
        Assert.Null(deB.EstabelecimentoId);
        Assert.DoesNotContain(c.Marcos.Lista, m => m.Mensagem == "wamid.inB");
    }

    [Fact]
    public async Task Numero_fora_da_gestao_e_telefone_invalido_sao_ignorados()
    {
        var c = new Cenario();
        Assert.False(await c.Retorno.RegistrarRecebidaAsync(Guid.NewGuid(), "5534999990001", "wamid.x", T0, "oi"));
        Assert.False(await c.Retorno.RegistrarRecebidaAsync(CanalA, "123", "wamid.y", T0, "oi"));
        Assert.Empty(c.Store.Mensagens);
        Assert.Empty(c.Marcos.Lista);
    }

    // ------------------------------------------------------------------------------------- idempotencia

    [Fact]
    public async Task Mesma_chave_nao_envia_duas_vezes_e_chave_de_outro_destino_da_409()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        var primeira = await c.Enviar(c.AtorAna, $"numero:{CanalA}", chave: "k1");
        var segunda = await c.Enviar(c.AtorAna, $"numero:{CanalA}", chave: "k1");
        Assert.True(segunda.Repetida);
        Assert.Equal(primeira.Id, segunda.Id);
        Assert.Single(c.Meta.Enviados);

        var outra = await Falha(() => c.Enviar(c.AtorAna, "pessoal", lead: Lead, chave: "k1"));
        Assert.Equal(409, outra.Status);
        Assert.Equal(CodigosGestaoMensagem.ChaveReutilizada, outra.Codigo);

        Assert.Equal(422, (await Falha(() => c.Service.EnviarAsync(c.AtorAna, new EnviarMensagemRequest(Loja, null, null, "painel", "x", null, null, null, false, null), default))).Status);
        Assert.Equal(422, (await Falha(() => c.Service.EnviarAsync(c.AtorAna, new EnviarMensagemRequest(Loja, null, null, "painel", "x", new ModeloEnvio(null, "m", null, null), "k", null, false, null), default))).Status);
        Assert.Equal(422, (await Falha(() => c.Service.EnviarAsync(c.AtorAna, new EnviarMensagemRequest(null, null, null, "painel", "x", null, "k9", null, false, null), default))).Status);
    }

    [Fact]
    public async Task Lead_recebe_pelo_numero_da_zippy_e_o_marco_vai_para_a_oportunidade()
    {
        var c = new Cenario();
        await c.Recebida(CanalA, "wamid.in1");
        var r = await c.Enviar(c.AtorAna, $"numero:{CanalA}", lead: Lead);
        Assert.Equal(EstadoMensagem.Enviado, r.Estado);
        Assert.Equal(Lead, c.Marcos.Lista.Single().Ref);
        Assert.Single(await c.Service.HistoricoAsync(c.AtorAna, null, Lead, default));
    }

    // ------------------------------------------------------------------------------------- regras puras

    [Theory]
    [InlineData("+5534999990001", "553499990001")]
    [InlineData("5534999990001", "553499990001")]
    [InlineData("553499990001", "553499990001")]      // celular antigo, sem o nono digito
    [InlineData("(34) 99999-0001", "553499990001")]
    [InlineData("34 9999-0001", "553499990001")]
    public void Chave_de_telefone_ignora_formato_e_nono_digito(string entrada, string esperado) =>
        Assert.Equal(esperado, GestaoMensagensRegras.ChaveTelefone(entrada));

    [Fact]
    public void Chave_de_telefone_rejeita_numero_curto() => Assert.Null(GestaoMensagensRegras.ChaveTelefone("1234"));

    [Theory]
    [InlineData("enviando", "sent", "enviado")]
    [InlineData("enviado", "delivered", "entregue")]
    [InlineData("entregue", "read", "lido")]
    [InlineData("enviando", "failed", "falhou")]
    [InlineData("enviado", "failed", "falhou")]
    [InlineData("entregue", "failed", null)]
    [InlineData("lido", "delivered", null)]
    [InlineData("falhou", "delivered", null)]
    [InlineData("enviado_por_fora", "read", null)]
    [InlineData("aguardando_confirmacao", "delivered", null)]
    [InlineData("enviado", "deleted", null)]
    public void Recibo_nunca_regride_e_nao_mexe_em_estado_externo(string atual, string status, string? esperado) =>
        Assert.Equal(esperado, GestaoMensagensRegras.AplicarRecibo(atual, status));

    [Fact]
    public void Variaveis_contadas_preenchidas_e_validadas()
    {
        Assert.Equal(2, GestaoMensagensRegras.ContarVariaveis("Ola {{1}}, {{2}} e {{1}}"));
        Assert.Equal("Ola Ana, hoje", GestaoMensagensRegras.Preencher("Ola {{1}}, {{2}}", new[] { "Ana", "hoje" }));
        Assert.Null(GestaoMensagensRegras.ErroVariaveis(1, new[] { "ok" }));
        Assert.NotNull(GestaoMensagensRegras.ErroVariaveis(1, new[] { " " }));
        Assert.NotNull(GestaoMensagensRegras.ErroVariaveis(2, new[] { "a" }));
        Assert.NotNull(GestaoMensagensRegras.ErroVariaveis(1, new[] { "a    b" }));
    }
}
