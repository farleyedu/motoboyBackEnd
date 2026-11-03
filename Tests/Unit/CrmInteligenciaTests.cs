using System.Text.Json;
using APIBack.AssistenteComercial.Audio;
using APIBack.AssistenteComercial.Inteligencia;
using Microsoft.Extensions.Options;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>
/// Objecoes, recomendacoes e coaching: evidencia obrigatoria, hipotese nao vira fato, texto de IA que inventa e descartado, nenhuma probabilidade,
/// amostra pequena rende "sem evidencia suficiente" e opt-out do audio e respeitado. Store, CRM, catalogo e redator sao fakes em memoria.
/// </summary>
public class CrmInteligenciaTests
{
    private const int Ana = 7, Beto = 8, Cris = 9, Admin = 1;
    private static readonly Guid Opp = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid OppDoBeto = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------- fakes ----------

    private sealed class Relogio : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T0;
    }

    private sealed class Opcoes : IOptionsMonitor<InteligenciaOptions>
    {
        public InteligenciaOptions CurrentValue { get; set; } = new() { AmostraMinima = 5, JanelaCoachingDias = 90, MaxExemplos = 3 };
        public InteligenciaOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<InteligenciaOptions, string?> listener) => null;
    }

    private sealed class Crm : IAudioCrm
    {
        public Task<IReadOnlyList<OportunidadeParaMatch>> OportunidadesDoUsuarioAsync(int u, CancellationToken ct) => throw new NotSupportedException();
        public Task<OportunidadeCrm?> ObterAsync(Guid id, CancellationToken ct) => Task.FromResult<OportunidadeCrm?>(
            id == Opp ? new OportunidadeCrm(Opp, "Pizzaria Bella Massa", "Carlos", "ativo", Ana, null)
            : id == OppDoBeto ? new OportunidadeCrm(OppDoBeto, "Padaria", null, "ativo", Beto, null) : null);
        public Task<IReadOnlyList<string>> HistoricoAsync(Guid id, int limite, CancellationToken ct) => throw new NotSupportedException();
        public Task AdicionarNotaAsync(Guid id, int u, string a, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task DefinirProximaAcaoAsync(Guid id, int u, string v, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Equipe : IVerificacaoFuncionario
    {
        public Task<bool> VerificadoAsync(int u, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> EquipeCrmAsync(int u, CancellationToken ct) => Task.FromResult(u is Ana or Beto);
    }

    private sealed class Catalogo : ICatalogoComercial
    {
        public IReadOnlyCollection<string>? Modulos { get; set; } = new[] { "gestao_caixa", "Entregas" };
        public Task<IReadOnlyCollection<string>> ModulosAtivosAsync(CancellationToken ct) =>
            Modulos == null ? throw new InvalidOperationException("catalogo fora do ar") : Task.FromResult(Modulos);
    }

    private sealed class Redator : IRedatorRecomendacao
    {
        public RedacaoIa? Resposta { get; set; }
        public bool Falha { get; set; }
        public int Chamadas { get; private set; }
        public Task<RedacaoIa?> RedigirAsync(RecomendacaoCandidata c, IReadOnlyList<FatoParaRedacao> fatos, IReadOnlyCollection<string> catalogo, CancellationToken ct)
        {
            Chamadas++;
            if (Falha) throw new HttpRequestException("ia fora do ar");
            return Task.FromResult(Resposta == null ? null : Resposta with { FatosUsados = Resposta.FatosUsados.Count == 0 ? new[] { fatos[0].Id } : Resposta.FatosUsados });
        }
    }

    private sealed class Store : IInteligenciaStore
    {
        public readonly List<Objecao> Objecoes = new();
        public readonly List<Recomendacao> Recomendacoes = new();
        public readonly List<(Guid Obj, string Evento, int Ator, string? Motivo, string? Antes, string? Depois)> Auditoria = new();
        public readonly List<(Guid Rec, string Evento)> Eventos = new();
        public readonly List<EntradaAudioConfirmada> Entradas = new();
        public readonly HashSet<int> Consentidos = new() { Ana, Beto };
        public readonly List<ObjecaoHistorica> Historico = new();

        public Task<IReadOnlyList<Objecao>> ListarObjecoesAsync(Guid opp, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Objecao>>(Objecoes.Where(o => o.OportunidadeId == opp).Select(o => o.Copia()).ToList());
        public Task<Objecao?> ObterObjecaoAsync(Guid id, CancellationToken ct) => Task.FromResult(Objecoes.FirstOrDefault(o => o.Id == id)?.Copia());
        public Task<bool> InserirObjecaoAsync(Objecao o, CancellationToken ct)
        {
            if (o.EntradaAudioId != null && Objecoes.Any(x => x.EntradaAudioId == o.EntradaAudioId && x.Evidencia == o.Evidencia)) return Task.FromResult(false);
            var c = o.Copia(); c.Versao = 1; c.CriadoEm = c.AtualizadoEm = T0.UtcDateTime; Objecoes.Add(c);
            return Task.FromResult(true);
        }
        public Task<bool> AtualizarObjecaoAsync(Objecao o, int lida, CancellationToken ct)
        {
            var i = Objecoes.FindIndex(x => x.Id == o.Id);
            if (i < 0 || Objecoes[i].Versao != lida) return Task.FromResult(false);
            var c = o.Copia(); c.Versao = lida + 1; Objecoes[i] = c;
            return Task.FromResult(true);
        }
        public Task AuditarObjecaoAsync(Guid obj, Guid opp, string evento, int ator, object? antes, object? depois, string? motivo, CancellationToken ct)
        {
            Auditoria.Add((obj, evento, ator, motivo, antes == null ? null : JsonSerializer.Serialize(antes, Web), depois == null ? null : JsonSerializer.Serialize(depois, Web)));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EntradaAudioConfirmada>> EntradasAudioConfirmadasAsync(Guid opp, CancellationToken ct) => Task.FromResult<IReadOnlyList<EntradaAudioConfirmada>>(Entradas);
        public Task<bool> ConsentimentoAudioAsync(int u, CancellationToken ct) => Task.FromResult(Consentidos.Contains(u));

        public Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Guid opp, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Recomendacao>>(Recomendacoes.Where(r => r.OportunidadeId == opp).Select(r => r.Copia()).ToList());
        public Task<Recomendacao?> ObterRecomendacaoAsync(Guid id, CancellationToken ct) => Task.FromResult(Recomendacoes.FirstOrDefault(r => r.Id == id)?.Copia());
        public Task<bool> InserirRecomendacaoAsync(Recomendacao r, CancellationToken ct)
        {
            if (Recomendacoes.Any(x => x.OportunidadeId == r.OportunidadeId && x.ImpressaoDigital == r.ImpressaoDigital && x.Estado is "sugerida" or "aceita")) return Task.FromResult(false);
            var c = r.Copia(); c.Versao = 1; c.CriadoEm = c.AtualizadoEm = T0.UtcDateTime; Recomendacoes.Add(c);
            return Task.FromResult(true);
        }
        public Task<bool> AtualizarRecomendacaoAsync(Recomendacao r, int lida, CancellationToken ct)
        {
            var i = Recomendacoes.FindIndex(x => x.Id == r.Id);
            if (i < 0 || Recomendacoes[i].Versao != lida) return Task.FromResult(false);
            var c = r.Copia(); c.Versao = lida + 1; Recomendacoes[i] = c;
            return Task.FromResult(true);
        }
        public Task RegistrarEventoAsync(Guid rec, string evento, int? ator, object? detalhe, CancellationToken ct) { Eventos.Add((rec, evento)); return Task.CompletedTask; }

        public Task<IReadOnlyList<ObjecaoHistorica>> ObjecoesResolvidasComAbordagemAsync(string[] tipos, Guid exceto, int limite, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ObjecaoHistorica>>(Historico.Where(h => tipos.Contains(h.Tipo) && h.OportunidadeId != exceto).ToList());
        public Task<IReadOnlyList<ObjecaoDoResponsavel>> ObjecoesDoResponsavelAsync(int u, DateTime desde, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ObjecaoDoResponsavel>>(Objecoes.Where(o => (o.OportunidadeId == Opp ? Ana : Beto) == u)
                .Select(o => new ObjecaoDoResponsavel(o.Id, o.OportunidadeId, o.Tipo, o.Estado, o.Certeza, o.Impacto, o.Evidencia, o.Abordagem, o.OcorridaEm, o.RespostaPrometidaAte)).ToList());
        public Task<IReadOnlyList<RecomendacaoDoResponsavel>> RecomendacoesDoResponsavelAsync(int u, DateTime desde, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecomendacaoDoResponsavel>>(Recomendacoes.Where(r => (r.OportunidadeId == Opp ? Ana : Beto) == u)
                .Select(r => new RecomendacaoDoResponsavel(r.Id, r.OportunidadeId, r.Regra, r.Estado, r.Reacao, r.Resultado, r.CriadoEm)).ToList());
    }

    private sealed class Ctx
    {
        public Store Store { get; } = new();
        public Catalogo Catalogo { get; } = new();
        public Redator Redator { get; } = new();
        public Opcoes Opcoes { get; } = new();
        public InteligenciaService Service => new(Store, new Crm(), new Equipe(), Catalogo, Redator, Opcoes, new Relogio());
    }

    private static RegistrarObjecaoRequest Nova(string tipo = "preco", string evidencia = "Carlos disse: achou caro os 299 por mes") => new()
    {
        Tipo = tipo, Descricao = "Acha o plano caro", Evidencia = evidencia,
    };

    private static async Task<InteligenciaException> Falha(Func<Task> acao) => await Assert.ThrowsAsync<InteligenciaException>(acao);

    private static Objecao Semear(Ctx c, string tipo = "preco", string estado = "levantada", string impacto = "desconhecido", string certeza = "declarada",
        string? modulo = null, DateTime? prazo = null, string? abordagem = null, Guid? opp = null)
    {
        var o = new Objecao
        {
            Id = Guid.NewGuid(), OportunidadeId = opp ?? Opp, Tipo = tipo, Descricao = "d", Evidencia = $"evidencia {tipo}", Origem = "manual", Certeza = certeza, Estado = estado,
            Impacto = impacto, ModuloCitado = modulo, RespostaPrometidaAte = prazo, Abordagem = abordagem, OcorridaEm = T0.UtcDateTime.AddDays(-3), CriadoPor = Ana,
        };
        c.Store.InserirObjecaoAsync(o, default).GetAwaiter().GetResult();
        return o;
    }

    // ---------- objecoes: insercao manual, evidencia, auditoria ----------

    [Fact]
    public async Task Objecoes_sao_inseridas_sem_IA_varias_por_oportunidade_e_auditadas()
    {
        var c = new Ctx();
        var a = await c.Service.RegistrarObjecaoAsync(Ana, false, Opp, Nova("preco"), default);
        var b = await c.Service.RegistrarObjecaoAsync(Ana, false, Opp, Nova("concorrente", "Usa o Saipos hoje"), default);

        Assert.Equal(0, c.Redator.Chamadas);
        Assert.Equal("manual", a.Origem);
        Assert.Equal("declarada", a.Certeza);
        Assert.Equal("levantada", a.Estado);
        var lista = await c.Service.ListarObjecoesAsync(Ana, false, Opp, default);
        Assert.Equal(2, lista.Objecoes.Count);
        Assert.Equal(2, c.Store.Auditoria.Count(x => x.Evento == "criada" && x.Ator == Ana));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Objecao_sem_evidencia_e_recusada_422(string? evidencia)
    {
        var c = new Ctx();
        var ex = await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, Nova(evidencia: evidencia!), default));
        Assert.Equal(422, ex.Status);
        Assert.Equal(CodigoInteligencia.EvidenciaAusente, ex.Codigo);
        Assert.Empty(c.Store.Objecoes);
    }

    [Fact]
    public async Task Hipotese_nao_pode_ser_impeditiva_nem_vira_barreira()
    {
        var c = new Ctx();
        var req = Nova(); req.Certeza = "hipotese"; req.Estado = "impeditiva";
        var ex = await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, req, default));
        Assert.Equal(CodigoInteligencia.HipoteseComoFato, ex.Codigo);

        // hipotese com modulo ausente do catalogo nao e tratada como barreira real: so pede diagnostico
        Semear(c, "funcionalidade", certeza: "hipotese", modulo: "Cardapio digital");
        var recs = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        Assert.DoesNotContain(recs, r => r.Regra == RegraRecomendacao.BarreiraReal);
        Assert.Contains(recs, r => r.Regra == RegraRecomendacao.Diagnostico && r.Hipotese.Contains("hipotese"));
    }

    [Fact]
    public async Task Valores_invalidos_rendem_422()
    {
        var c = new Ctx();
        var r1 = Nova("tipo_que_nao_existe");
        Assert.Equal(422, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, r1, default))).Status);
        var r2 = Nova(); r2.Estado = "perdida";
        Assert.Equal(422, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, r2, default))).Status);
        var r3 = Nova(); r3.Descricao = "";
        Assert.Equal(422, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, r3, default))).Status);
    }

    [Fact]
    public async Task Correcao_exige_motivo_e_versao_e_guarda_antes_e_depois()
    {
        var c = new Ctx();
        var criada = await c.Service.RegistrarObjecaoAsync(Ana, false, Opp, Nova(), default);

        var semMotivo = new RegistrarObjecaoRequest { Id = criada.Id, Estado = "investigando", VersaoEsperada = criada.Versao };
        Assert.Equal(CodigoInteligencia.MotivoAusente, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, semMotivo, default))).Codigo);

        var versaoVelha = new RegistrarObjecaoRequest { Id = criada.Id, Estado = "investigando", MotivoCorrecao = "x", VersaoEsperada = criada.Versao + 5 };
        Assert.Equal(409, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, versaoVelha, default))).Status);

        var ok = new RegistrarObjecaoRequest { Id = criada.Id, Estado = "investigando", Impacto = "alto", MotivoCorrecao = "Cliente confirmou por telefone", VersaoEsperada = criada.Versao };
        var nova = await c.Service.RegistrarObjecaoAsync(Ana, false, Opp, ok, default);

        Assert.Equal("investigando", nova.Estado);
        Assert.Equal("alto", nova.Impacto);
        Assert.Equal(criada.Versao + 1, nova.Versao);
        var aud = Assert.Single(c.Store.Auditoria, x => x.Evento == "corrigida");
        Assert.Equal("Cliente confirmou por telefone", aud.Motivo);
        Assert.Contains("\"estado\":\"levantada\"", aud.Antes);
        Assert.Contains("\"estado\":\"investigando\"", aud.Depois);
    }

    [Fact]
    public async Task Correcao_de_objecao_de_outra_oportunidade_e_404()
    {
        var c = new Ctx();
        var alheia = Semear(c, opp: OppDoBeto);
        var req = new RegistrarObjecaoRequest { Id = alheia.Id, Estado = "resolvida", MotivoCorrecao = "x", VersaoEsperada = 1 };
        Assert.Equal(404, (await Falha(() => c.Service.RegistrarObjecaoAsync(Ana, false, Opp, req, default))).Status);
    }

    // ---------- escopo ----------

    [Fact]
    public async Task Escopo_equipe_responsavel_e_super_admin()
    {
        var c = new Ctx();
        Assert.Equal(403, (await Falha(() => c.Service.ListarObjecoesAsync(Cris, false, Opp, default))).Status);        // fora da equipe
        Assert.Equal(403, (await Falha(() => c.Service.ListarObjecoesAsync(Beto, false, Opp, default))).Status);        // oportunidade de outro responsavel
        Assert.Equal(404, (await Falha(() => c.Service.ListarObjecoesAsync(Ana, false, Guid.NewGuid(), default))).Status);
        Assert.Equal(403, (await Falha(() => c.Service.RecomendacoesAsync(Beto, false, Opp, default))).Status);
        Assert.Equal(403, (await Falha(() => c.Service.FeedbackAsync(Beto, false, Opp, Guid.NewGuid(), new FeedbackRequest { Evento = "aceita" }, default))).Status);
        Assert.NotNull(await c.Service.ListarObjecoesAsync(Admin, true, Opp, default));                                 // super admin enxerga
    }

    // ---------- audio: so fato confirmado, opt-out respeitado ----------

    private static EntradaAudioConfirmada EntradaConfirmada(bool aplicada = true)
    {
        var proposta = new PropostaVisita();
        proposta.Fatos.Objecoes.Add(new ItemTexto("achou caro", "achou caro os 299"));
        proposta.Fatos.Objecoes.Add(new ItemTexto("quer integrar com o iFood", "queria integrar com o iFood"));
        proposta.Inferencias.Add(new Inferencia("Preco e a principal barreira", "media", "achou caro"));
        proposta.Alteracoes.Add(new AlteracaoProposta("a1", TipoAlteracao.NotaHistorico, "Objecao: achou caro", null, "achou caro os 299"));
        proposta.Alteracoes.Add(new AlteracaoProposta("a2", TipoAlteracao.NotaHistorico, "Objecao: quer integrar com o iFood", null, "queria integrar com o iFood"));
        var resultados = new List<ResultadoAlteracao>
        {
            new("a1", TipoAlteracao.NotaHistorico, aplicada ? "aplicada" : "nao_aprovada", null),
            new("a2", TipoAlteracao.NotaHistorico, "nao_aprovada", null),
        };
        return new EntradaAudioConfirmada
        {
            Id = Guid.NewGuid(), UsuarioId = Ana, PropostaJson = JsonSerializer.Serialize(proposta, Web), ResultadoJson = JsonSerializer.Serialize(resultados, Web),
            AtualizadaEm = T0.UtcDateTime.AddHours(-2),
        };
    }

    [Fact]
    public async Task Audio_importa_so_o_fato_declarado_e_aprovado_nunca_a_inferencia_e_e_idempotente()
    {
        var c = new Ctx();
        c.Store.Entradas.Add(EntradaConfirmada());

        var lista = await c.Service.ListarObjecoesAsync(Ana, false, Opp, default);
        var o = Assert.Single(lista.Objecoes);
        Assert.Equal(1, lista.ImportadasDoAudio);
        Assert.Equal("audio", o.Origem);
        Assert.Equal("declarada", o.Certeza);
        Assert.Equal("achou caro os 299", o.Evidencia);       // trecho literal, nao a leitura da IA
        Assert.Equal("preco", o.Tipo);
        Assert.NotNull(o.EntradaAudioId);
        Assert.Contains(c.Store.Auditoria, a => a.Evento == "importada_audio");

        var de_novo = await c.Service.ListarObjecoesAsync(Ana, false, Opp, default);
        Assert.Single(de_novo.Objecoes);
        Assert.Equal(0, de_novo.ImportadasDoAudio);
    }

    [Fact]
    public async Task Audio_com_nota_nao_aprovada_ou_sem_consentimento_nao_entra()
    {
        var c = new Ctx();
        c.Store.Entradas.Add(EntradaConfirmada(aplicada: false));
        Assert.Empty((await c.Service.ListarObjecoesAsync(Ana, false, Opp, default)).Objecoes);

        var c2 = new Ctx();
        c2.Store.Entradas.Add(EntradaConfirmada());
        c2.Store.Consentidos.Remove(Ana); // opt-out do vendedor autor do audio
        Assert.Empty((await c2.Service.ListarObjecoesAsync(Ana, false, Opp, default)).Objecoes);
    }

    // ---------- motor: prioridade, barreira real, capacidade do produto ----------

    [Fact]
    public async Task Modulo_fora_do_catalogo_vira_barreira_real_com_o_catalogo_como_evidencia_e_vem_primeiro()
    {
        var c = new Ctx();
        Semear(c, "preco");                                              // diagnostico
        Semear(c, "funcionalidade", impacto: "alto", modulo: "Controle de mesas");
        var recs = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);

        var primeira = recs[0];
        Assert.Equal(RegraRecomendacao.BarreiraReal, primeira.Regra);
        Assert.Equal(1, primeira.Prioridade);
        Assert.Contains("nao prometer", primeira.Sugestao);
        Assert.Contains(primeira.Evidencias, e => e.Tipo == "catalogo" && e.Texto.Contains("gestao_caixa"));
        Assert.Contains(primeira.Evidencias, e => e.Tipo == "objecao");
        Assert.DoesNotContain("prazo de entrega", primeira.Sugestao);
    }

    [Fact]
    public async Task Modulo_presente_no_catalogo_mesmo_com_grafia_diferente_nao_gera_barreira()
    {
        var c = new Ctx();
        Semear(c, "funcionalidade", impacto: "alto", modulo: "Gestão de Caixa");
        var recs = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        Assert.DoesNotContain(recs, r => r.Regra == RegraRecomendacao.BarreiraReal);
    }

    [Fact]
    public async Task Catalogo_indisponivel_com_modulo_citado_rende_503_em_vez_de_afirmar_capacidade()
    {
        var c = new Ctx();
        c.Catalogo.Modulos = null;
        Semear(c, "funcionalidade", impacto: "alto", modulo: "Controle de mesas");
        var ex = await Falha(() => c.Service.RecomendacoesAsync(Ana, false, Opp, default));
        Assert.Equal(503, ex.Status);
    }

    [Fact]
    public async Task Ordem_barreira_diagnostico_compromisso_abordagem()
    {
        var c = new Ctx();
        var ontem = T0.UtcDateTime.AddDays(-1);
        Semear(c, "concorrente", impacto: "alto", prazo: ontem);                    // compromisso vencido
        Semear(c, "preco");                                                          // diagnostico (impacto desconhecido)
        Semear(c, "prazo", estado: "impeditiva", impacto: "alto");                   // barreira real (impeditiva declarada)
        Semear(c, "integracao", impacto: "alto");                                    // alvo de abordagem comparavel
        for (var i = 0; i < 5; i++)
            c.Store.Historico.Add(new ObjecaoHistorica(Guid.NewGuid(), Guid.NewGuid(), "integracao", $"Mostrou a integracao em video {i}", "ev", T0.UtcDateTime.AddDays(-i)));

        var recs = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);

        Assert.Equal(new[] { 1, 2, 3, 4 }, recs.Select(r => r.Prioridade).ToArray());
        Assert.Equal(new[] { RegraRecomendacao.BarreiraReal, RegraRecomendacao.Diagnostico, RegraRecomendacao.CompromissoVencido, RegraRecomendacao.AbordagemComparavel },
            recs.Select(r => r.Regra).ToArray());
        var comparavel = recs[3];
        Assert.Contains("associacao", comparavel.Hipotese);
        Assert.Contains(comparavel.Evidencias, e => e.Tipo == "amostra" && e.Texto.StartsWith("5 "));
        Assert.Equal(3, comparavel.Evidencias.Count(e => e.Tipo == "caso_comparavel"));
    }

    [Fact]
    public async Task Abordagem_comparavel_com_poucos_casos_nao_e_sugerida()
    {
        var c = new Ctx();
        Semear(c, "integracao", impacto: "alto");
        for (var i = 0; i < 3; i++)
            c.Store.Historico.Add(new ObjecaoHistorica(Guid.NewGuid(), Guid.NewGuid(), "integracao", "abordagem", "ev", T0.UtcDateTime));
        // varias objecoes da MESMA oportunidade contam uma vez so
        var mesma = Guid.NewGuid();
        for (var i = 0; i < 6; i++) c.Store.Historico.Add(new ObjecaoHistorica(Guid.NewGuid(), mesma, "integracao", "abordagem", "ev", T0.UtcDateTime));

        var recs = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        Assert.DoesNotContain(recs, r => r.Regra == RegraRecomendacao.AbordagemComparavel);
    }

    [Fact]
    public async Task Recomendacoes_sao_idempotentes_e_sugestao_sem_base_some()
    {
        var c = new Ctx();
        var o = Semear(c, "preco");
        var a = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        var b = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        Assert.Single(a);
        Assert.Single(b);
        Assert.Single(c.Store.Recomendacoes);
        Assert.Single(c.Store.Eventos, e => e.Evento == "gerada");

        // objecao resolvida: a sugestao que ninguem decidiu deixa de aparecer
        o.Estado = "resolvida";
        c.Store.Objecoes[0] = o;
        Assert.Empty(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
    }

    // ---------- IA: so redige, nunca inventa ----------

    private static RedacaoIa Redacao(string texto, string[]? modulos = null) => new(texto, Array.Empty<string>(), modulos ?? Array.Empty<string>());

    [Fact]
    public async Task Redacao_valida_da_IA_e_guardada_e_marcada()
    {
        var c = new Ctx();
        Semear(c, "preco");
        c.Redator.Resposta = Redacao("Pergunte o que ele compara com o valor atual antes de responder.");
        var rec = Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
        Assert.Equal("Pergunte o que ele compara com o valor atual antes de responder.", rec.RedacaoIa);
        Assert.Equal("regra_com_redacao_ia_validada", rec.Natureza);
        Assert.NotEmpty(rec.Acao);   // texto deterministico continua presente
    }

    [Theory]
    [InlineData("Ofereca 20 por cento de desconto no primeiro mes.")]
    [InlineData("Diga que o modulo chega em breve e ja esta garantido.")]
    [InlineData("Com isso a chance de fechar sobe para 80%.")]
    [InlineData("Ele recusou por causa do preco, entao baixe o valor.")]
    [InlineData("Cite o valor de 249 reais ao cliente.")]            // numero que nao esta em nenhum fato
    public async Task Texto_de_IA_que_inventa_e_descartado_e_a_recomendacao_segue_deterministica(string alucinacao)
    {
        var c = new Ctx();
        Semear(c, "preco");
        c.Redator.Resposta = Redacao(alucinacao);
        var rec = Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
        Assert.Null(rec.RedacaoIa);
        Assert.Equal("regra_baseada_em_fatos_registrados", rec.Natureza);
        Assert.Equal(1, c.Redator.Chamadas);
    }

    [Fact]
    public async Task IA_que_cita_modulo_fora_do_catalogo_ou_fato_inexistente_e_descartada()
    {
        var c = new Ctx();
        Semear(c, "preco");
        c.Redator.Resposta = Redacao("Apresente o modulo Controle de mesas.", new[] { "Controle de mesas" });
        Assert.Null(Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default)).RedacaoIa);

        var fatos = new[] { new FatoParaRedacao("objecao:1", "texto") };
        var catalogo = new[] { "gestao_caixa" };
        Assert.False(GuardaRedacao.Valida(new RedacaoIa("Pergunte antes.", new[] { "fato:que-nao-existe" }, Array.Empty<string>()), fatos, catalogo, out var m1));
        Assert.Contains("inexistente", m1);
        Assert.False(GuardaRedacao.Valida(new RedacaoIa("Pergunte antes.", Array.Empty<string>(), Array.Empty<string>()), fatos, catalogo, out var m2));
        Assert.Contains("sem referencia", m2);
        Assert.True(GuardaRedacao.Valida(new RedacaoIa("Pergunte antes.", new[] { "objecao:1" }, new[] { "Gestão de Caixa" }), fatos, catalogo, out _));
    }

    [Fact]
    public async Task IA_fora_do_ar_nao_derruba_a_recomendacao()
    {
        var c = new Ctx();
        Semear(c, "preco");
        c.Redator.Falha = true;
        var rec = Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
        Assert.Null(rec.RedacaoIa);
        Assert.NotEmpty(rec.Sugestao);
    }

    [Fact]
    public async Task Opt_out_do_responsavel_desliga_a_redacao_por_IA_mas_nao_a_regra()
    {
        var c = new Ctx();
        Semear(c, "preco");
        c.Store.Consentidos.Remove(Ana);
        c.Redator.Resposta = Redacao("Pergunte antes.");
        var rec = Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
        Assert.Equal(0, c.Redator.Chamadas);
        Assert.Null(rec.RedacaoIa);
    }

    [Fact]
    public async Task Nenhuma_saida_carrega_probabilidade_percentual_ou_desconto()
    {
        var c = new Ctx();
        Semear(c, "preco");
        Semear(c, "funcionalidade", impacto: "alto", modulo: "Controle de mesas");
        Semear(c, "prazo", estado: "impeditiva", impacto: "alto", prazo: T0.UtcDateTime.AddDays(-2));
        var json = JsonSerializer.Serialize(await c.Service.RecomendacoesAsync(Ana, false, Opp, default), Web).ToLowerInvariant();

        Assert.DoesNotContain("probabilidade", json);
        Assert.DoesNotContain("%", json);
        Assert.DoesNotContain("desconto", json);
        Assert.DoesNotContain("chance", json);
        Assert.False(GuardaRedacao.Valida(Redacao("80% de chance de fechar"), new[] { new FatoParaRedacao("a:1", "80") }, Array.Empty<string>(), out _));
    }

    // ---------- feedback: ciclo completo, sem atribuicao causal ----------

    private static async Task<RecomendacaoDto> UmaRecomendacao(Ctx c)
    {
        Semear(c, "preco");
        return Assert.Single(await c.Service.RecomendacoesAsync(Ana, false, Opp, default));
    }

    [Fact]
    public async Task Ciclo_sugerida_aceita_executada_reacao_resultado()
    {
        var c = new Ctx();
        var r = await UmaRecomendacao(c);
        Assert.Equal("sugerida", r.Estado);

        r = await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "aceita", VersaoEsperada = r.Versao }, default);
        Assert.Equal("aceita", r.Estado);
        r = await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "executada", Observacao = "Liguei hoje" }, default);
        Assert.Equal("executada", r.Estado);
        r = await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "reacao", Reacao = "positiva" }, default);
        r = await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "resultado", Resultado = "avancou" }, default);

        Assert.Equal("positiva", r.Reacao);
        Assert.Equal("avancou", r.Resultado);
        Assert.Equal("resultado_registrado_sem_atribuicao_causal", r.Natureza);
        Assert.Equal(new[] { "gerada", "aceita", "executada", "reacao", "resultado" }, c.Store.Eventos.Select(e => e.Evento).ToArray());
    }

    [Fact]
    public async Task Rejeitar_guarda_motivo_e_a_mesma_situacao_nao_volta_a_ser_sugerida()
    {
        var c = new Ctx();
        var r = await UmaRecomendacao(c);
        var rej = await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "rejeitada", Motivo = "Ja conversei com ele" }, default);
        Assert.Equal("rejeitada", rej.Estado);
        Assert.Equal("Ja conversei com ele", rej.MotivoRejeicao);

        var depois = await c.Service.RecomendacoesAsync(Ana, false, Opp, default);
        Assert.Single(depois);
        Assert.Equal("rejeitada", depois[0].Estado);
        Assert.Single(c.Store.Recomendacoes);
    }

    [Fact]
    public async Task Transicoes_invalidas_versao_velha_e_dados_invalidos()
    {
        var c = new Ctx();
        var r = await UmaRecomendacao(c);

        Assert.Equal(409, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "executada" }, default))).Status);
        Assert.Equal(409, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "resultado", Resultado = "avancou" }, default))).Status);
        Assert.Equal(409, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "aceita", VersaoEsperada = 99 }, default))).Status);
        Assert.Equal(422, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "teletransportar" }, default))).Status);
        Assert.Equal(422, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { }, default))).Status);

        await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "aceita" }, default);
        await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "executada" }, default);
        Assert.Equal(422, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "reacao", Reacao = "furiosa" }, default))).Status);
        await c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "reacao", Reacao = "neutra" }, default);
        Assert.Equal(409, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, r.Id, new FeedbackRequest { Evento = "reacao", Reacao = "positiva" }, default))).Status);
    }

    [Fact]
    public async Task Recomendacao_de_outra_oportunidade_ou_inexistente_e_404()
    {
        var c = new Ctx();
        var r = await UmaRecomendacao(c);
        Assert.Equal(404, (await Falha(() => c.Service.FeedbackAsync(Ana, false, Opp, Guid.NewGuid(), new FeedbackRequest { Evento = "aceita" }, default))).Status);
        Assert.Equal(404, (await Falha(() => c.Service.FeedbackAsync(Admin, true, OppDoBeto, r.Id, new FeedbackRequest { Evento = "aceita" }, default))).Status);
    }

    // ---------- coaching: amostra, exemplos e nunca inventar percentual ----------

    [Fact]
    public async Task Coaching_sem_dados_diz_sem_evidencia_suficiente()
    {
        var c = new Ctx();
        var r = await c.Service.CoachingAsync(Ana, false, null, default);
        var p = Assert.Single(r.Padroes);
        Assert.Equal("sem_dados", p.Id);
        Assert.False(p.Suficiente);
        Assert.Null(p.Percentual);
        Assert.Contains("Sem evidencia suficiente", p.Descricao);
    }

    [Fact]
    public async Task Coaching_com_amostra_pequena_nao_calcula_percentual_nem_da_exercicio()
    {
        var c = new Ctx();
        Semear(c, "preco", estado: "resolvida", abordagem: "Mostrei o retorno em tempo economizado");
        Semear(c, "preco");
        Semear(c, "preco");
        var r = await c.Service.CoachingAsync(Ana, false, null, default);

        Assert.All(r.Padroes, p => { Assert.False(p.Suficiente); Assert.Null(p.Percentual); Assert.Null(p.Exercicio); Assert.Contains("Sem evidencia suficiente", p.Descricao); });
        var resolucao = r.Padroes.Single(p => p.Id == "resolucao_preco");
        Assert.Equal(1, resolucao.Observados);
        Assert.Equal(3, resolucao.Amostra);
        Assert.Equal(5, r.AmostraMinima);
        Assert.DoesNotContain("%", JsonSerializer.Serialize(r, Web));
    }

    [Fact]
    public async Task Coaching_com_amostra_suficiente_mostra_fracao_real_exemplos_e_exercicio()
    {
        var c = new Ctx();
        for (var i = 0; i < 3; i++) Semear(c, "preco", estado: "resolvida", abordagem: $"abordagem {i}");
        for (var i = 0; i < 2; i++) Semear(c, "preco");
        var r = await c.Service.CoachingAsync(Ana, false, null, default);

        var p = r.Padroes.Single(x => x.Id == "resolucao_preco");
        Assert.True(p.Suficiente);
        Assert.Equal(3, p.Observados);
        Assert.Equal(5, p.Amostra);
        Assert.Equal(60.0, p.Percentual);
        Assert.Contains("3 de 5", p.Descricao);
        Assert.Equal(3, p.Exemplos.Count);
        Assert.NotNull(p.Exercicio);
        Assert.Equal(5, r.AmostraObjecoes);
    }

    [Fact]
    public async Task Coaching_hipotese_nao_entra_na_contagem_e_acesso_a_outra_pessoa_e_restrito()
    {
        var c = new Ctx();
        for (var i = 0; i < 5; i++) Semear(c, "preco", certeza: "hipotese");
        var r = await c.Service.CoachingAsync(Ana, false, null, default);
        Assert.Equal("sem_dados", Assert.Single(r.Padroes).Id);

        Assert.Equal(403, (await Falha(() => c.Service.CoachingAsync(Ana, false, Beto, default))).Status);
        Assert.Equal(403, (await Falha(() => c.Service.CoachingAsync(Cris, false, null, default))).Status);
        Assert.Equal(404, (await Falha(() => c.Service.CoachingAsync(Admin, true, Cris, default))).Status);
        Assert.Equal(Beto, (await c.Service.CoachingAsync(Admin, true, Beto, default)).ResponsavelUsuarioId);
    }

    // ---------- migration ----------

    [Fact]
    public void Migration_16_nao_guarda_probabilidade_nem_desconto_e_exige_evidencia()
    {
        var linhas = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261127_16_crm_inteligencia.sql"));
        var sql = string.Join("\n", linhas.Where(l => !l.TrimStart().StartsWith("--"))).ToLowerInvariant();
        Assert.Matches(@"evidencia\s+text not null", sql);
        Assert.Contains("ck_crm_objecao_hipotese_nao_impeditiva", sql);
        Assert.DoesNotContain("probabilidade", sql);
        Assert.DoesNotContain("desconto", sql);
    }
}
