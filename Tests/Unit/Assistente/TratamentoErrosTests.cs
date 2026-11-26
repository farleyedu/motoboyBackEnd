using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Assistente;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

/// <summary>Cada caso de tratamento de erro da cadeia (CONTRATO-API §10.3).</summary>
public sealed class TratamentoErrosTests
{
    private static readonly IReadOnlyList<JsonObject> Defs = FerramentasAssistente.Definicoes(["cardapio.pausar-produto"]);
    private static readonly DateTimeOffset Agora = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    // ------------------------------------------------------------------ ferramenta inválida: devolve uma vez, na segunda pula

    [Fact]
    public void FerramentaInexistenteVoltaParaAIaUmaVezENaSegundaDesiste()
    {
        var c = new ControleFerramentas("groq", Defs);
        Assert.Contains("não existe", c.Conferir("apagar_loja", Args("{}")));
        var ex = Assert.Throws<FalhaProvedorException>(() => c.Conferir("apagar_loja", Args("{}")));
        Assert.Equal((TipoFalha.Resposta, CatalogoFalhas.De(CodigosFalha.FerramentaErrada).Titulo), (ex.Tipo, ex.Message));
    }

    [Fact]
    public void JsonInvalidoEArgumentoRuimContamComoErro()
    {
        var c = new ControleFerramentas("groq", Defs);
        Assert.Contains("JSON válido", c.Conferir(FerramentasAssistente.Consultar, null));
        Assert.Throws<FalhaProvedorException>(() => c.Conferir(FerramentasAssistente.Consultar, Args("""{"tipo":"outra_loja"}""")));
    }

    [Fact]
    public void ChamadaCertaPassaEContaComoExecutada()
    {
        var c = new ControleFerramentas("groq", Defs);
        Assert.Null(c.Conferir(FerramentasAssistente.Consultar, Args("""{"tipo":"loja_status","busca":null}""")));
        Assert.True(c.ExecutouAlguma);
        c.ExigirConteudo(""); // sem texto mas com ferramenta (ex.: só cartão) não é vazia
    }

    [Fact]
    public void UmErroDepoisAcertoNaoDesiste()
    {
        var c = new ControleFerramentas("groq", Defs);
        Assert.NotNull(c.Conferir("x", Args("{}")));
        Assert.Null(c.Conferir(FerramentasAssistente.AbrirTela, Args("""{"funcionalidadeId":"loja.horario"}""")));
    }

    // ------------------------------------------------------------------ resposta vazia

    [Fact]
    public void RespostaVaziaSemFerramentaPulaOItem()
    {
        var ex = Assert.Throws<FalhaProvedorException>(() => new ControleFerramentas("gemini", Defs).ExigirConteudo("  "));
        Assert.Equal((TipoFalha.Resposta, CatalogoFalhas.De(CodigosFalha.RespostaVazia).Titulo), (ex.Tipo, ex.Message));
    }

    // ------------------------------------------------------------------ códigos HTTP

    [Theory]
    [InlineData(401, TipoFalha.Configuracao, CodigosFalha.ChaveRecusada)]
    [InlineData(403, TipoFalha.Configuracao, CodigosFalha.SemPermissao)]
    [InlineData(404, TipoFalha.Configuracao, CodigosFalha.ModeloNaoExiste)]
    [InlineData(429, TipoFalha.Limite, CodigosFalha.LimiteMinuto)]
    [InlineData(500, TipoFalha.Servidor, CodigosFalha.ErroInternoProvedor)]
    [InlineData(503, TipoFalha.Servidor, CodigosFalha.ModeloSobrecarregado)]
    [InlineData(402, TipoFalha.Configuracao, CodigosFalha.SemCredito)]
    public void StatusViraTipoECodigo(int status, TipoFalha tipo, string codigo)
    {
        var f = ProvedorOpenAI.PorStatus("groq", status, "detalhe");
        Assert.Equal((tipo, codigo, CatalogoFalhas.De(codigo).Titulo, status), (f.Tipo, f.Codigo, f.Message, f.Status!.Value));
    }

    [Fact]
    public void QuatrocentosGuardaSoOComecoDaMensagem()
    {
        var f = ProvedorOpenAI.PorStatus("groq", 400, new string('x', 1000));
        Assert.Equal(TipoFalha.Pedido, f.Tipo);
        Assert.True(f.MensagemProvedor!.Length <= MensagensFalha.MaxTamanho);
    }

    [Fact]
    public void CotaDoDiaNoQuatrocentosEVinteENove() =>
        Assert.True(ProvedorOpenAI.PorStatus("groq", 429, "Rate limit reached for requests per day (RPD)").CotaDiaria);

    // ------------------------------------------------------------------ tempo limite por chamada

    [Fact]
    public async Task PassouDoTempoViraFalhaDeServidor()
    {
        var ex = await Assert.ThrowsAsync<FalhaProvedorException>(() =>
            LimitesChamada.ComTempo("groq", async t => { await Task.Delay(5000, t); return 1; }, default, TimeSpan.FromMilliseconds(50)));
        Assert.Equal((TipoFalha.Servidor, CatalogoFalhas.De(CodigosFalha.TempoEsgotado).Titulo), (ex.Tipo, ex.Message));
    }

    [Fact]
    public async Task CancelamentoDeQuemPediuNaoViraFalha()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LimitesChamada.ComTempo("groq", async t => { await Task.Delay(5000, t); return 1; }, cts.Token, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TempoPadraoTrintaSegundos() => Assert.Equal(TimeSpan.FromSeconds(30), LimitesChamada.Tempo);

    // ------------------------------------------------------------------ parâmetro não suportado: tenta sem e aprende

    [Theory]
    [InlineData("Unrecognized request argument supplied: reasoning_effort", ProvedorOpenAI.ParamEsforco)]
    [InlineData("'strict' is not permitted", ProvedorOpenAI.ParamStrict)]
    [InlineData("Invalid JSON payload: additionalProperties is not supported", ProvedorOpenAI.ParamEsquema)]
    [InlineData("tools[0].function.parameters: invalid schema", ProvedorOpenAI.ParamEsquema)]
    [InlineData("model is required", null)]
    public void DescobreQualParametroFoiRecusado(string mensagem, string? param) => Assert.Equal(param, ProvedorOpenAI.ParametroRecusado(mensagem));

    [Fact]
    public void AprendidoDesligaAFlag()
    {
        var openai = ConfigIA.Padrao.ProvedorDe(Provedores.OpenAI)!;
        var foto = new FotoCadeia(new Dictionary<string, EstadoItem>(), new Dictionary<string, long>(),
            new Dictionary<string, IReadOnlySet<string>> { [Provedores.OpenAI] = new HashSet<string> { ProvedorOpenAI.ParamStrict } });
        var efetiva = RegrasCadeia.DefinicaoEfetiva(openai, foto);
        Assert.Equal((false, true, true), (efetiva.AceitaStrict, efetiva.AceitaEsforco, efetiva.EsquemaCompleto));
        Assert.Same(openai, RegrasCadeia.DefinicaoEfetiva(openai, FotoCadeia.Vazia));
    }

    [Fact]
    public void AdaptarSeparaStrictDeEsquema()
    {
        var propor = Defs.Single(d => d["name"]!.GetValue<string>() == FerramentasAssistente.ProporAcao);
        var soSemStrict = FerramentasAssistente.Adaptar(propor, strict: false, esquemaCompleto: true);
        Assert.Null(soSemStrict["strict"]);
        Assert.False(soSemStrict["input_schema"]!["additionalProperties"]!.GetValue<bool>());
        var soEsquema = FerramentasAssistente.Adaptar(propor, strict: true, esquemaCompleto: false);
        Assert.True(soEsquema["strict"]!.GetValue<bool>());
        Assert.Null(soEsquema["input_schema"]!["additionalProperties"]);
    }

    // ------------------------------------------------------------------ na cadeia

    private sealed class Falso(string id, Exception? falha = null, string? naoSuporta = null) : IProvedorIA
    {
        public int Chamadas;
        public string Nome => id;
        public bool TemChave => true;
        public Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct)
        {
            Chamadas++;
            if (naoSuporta is not null) pedido.NaoSuporta?.Invoke(naoSuporta);
            return falha is null ? Task.FromResult(new RespostaIA("ok", DesfechoIA.Normal, 1, 1, 0)) : Task.FromException<RespostaIA>(falha);
        }
        public Task<IReadOnlyList<string>> Modelos(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class Fabrica(params Falso[] p) : IFabricaProvedores
    {
        public IProvedorIA Criar(DefinicaoProvedor d) => p.First(x => x.Nome == d.Id);
        public bool TemChave(DefinicaoProvedor d) => true;
    }

    private sealed class Estado : IEstadoCadeiaRepositorio
    {
        public readonly List<(string Item, string Erro)> Desligados = [];
        public readonly List<string> Esgotados = [];
        public readonly List<(string, string)> Aprendidos = [];
        public Task<FotoCadeia> Foto(DateTimeOffset i, CancellationToken ct) => Task.FromResult(FotoCadeia.Vazia);
        public Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct) { Esgotados.Add(item.Id); return Task.CompletedTask; }
        public Task Desligar(ItemCadeia item, string erro, CancellationToken ct) { Desligados.Add((item.Id, erro)); return Task.CompletedTask; }
        public Task Religar(string itemId, CancellationToken ct) => Task.CompletedTask;
        public Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct) { Aprendidos.Add((provedor, parametro)); return Task.CompletedTask; }
        public Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Relogio : TimeProvider { public override DateTimeOffset GetUtcNow() => Agora; }

    private static readonly ConfigIA Cfg = ConfigIA.Padrao with
    {
        Cadeia = [new ItemCadeia("g", Provedores.Groq, "m-g"), new ItemCadeia("o", Provedores.OpenAI, "m-o")],
    };

    private static Task<RespostaComProvedor> Rodar(IFabricaProvedores f, Estado e) =>
        new CadeiaIA(f, e, new Relogio(), NullLogger<CadeiaIA>.Instance).Responder(Cfg,
            i => new PedidoIA(i.Modelo, "low", 1000, "s", Defs, [], "c", "t"), (_, _, _) => Task.FromResult(new ResultadoFerramenta("", false)), null, default);

    [Theory]
    [InlineData(CodigosFalha.RespostaVazia)]
    [InlineData(CodigosFalha.RespostaCortada)]
    [InlineData(CodigosFalha.ConteudoBloqueado)]
    [InlineData(CodigosFalha.FerramentaErrada)]
    [InlineData(CodigosFalha.VoltasDemais)]
    public async Task RespostaRuimPulaParaOProximoSemMarcar(string codigo)
    {
        var e = new Estado();
        var r = await Rodar(new Fabrica(new Falso(Provedores.Groq, CatalogoFalhas.Nova(Provedores.Groq, codigo)), new Falso(Provedores.OpenAI)), e);
        Assert.Equal(("o", true), (r.ItemId, r.Trocou));
        Assert.Empty(e.Esgotados);
        Assert.Empty(e.Desligados);
        Assert.Equal(MotivosPulo.PedidoRecusado, r.Pulos.Single().Motivo);
    }

    [Theory]
    [InlineData(401, "Chave recusada pelo provedor")]
    [InlineData(404, "Esse modelo não existe para esta chave")]
    public async Task ChaveOuModeloErradoDesligaComOMotivo(int status, string motivo)
    {
        var e = new Estado();
        var r = await Rodar(new Fabrica(new Falso(Provedores.Groq, ProvedorOpenAI.PorStatus(Provedores.Groq, status, null)), new Falso(Provedores.OpenAI)), e);
        Assert.Equal("o", r.ItemId);
        Assert.Equal([("g", motivo)], e.Desligados);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task LimiteOuServidorMarcaEsgotado(int status)
    {
        var e = new Estado();
        await Rodar(new Fabrica(new Falso(Provedores.Groq, ProvedorOpenAI.PorStatus(Provedores.Groq, status, null)), new Falso(Provedores.OpenAI)), e);
        Assert.Equal(["g"], e.Esgotados);
    }

    [Fact]
    public async Task TempoEsgotadoMarcaEsgotado()
    {
        var e = new Estado();
        await Rodar(new Fabrica(new Falso(Provedores.Groq, CatalogoFalhas.Nova(Provedores.Groq, CodigosFalha.TempoEsgotado)), new Falso(Provedores.OpenAI)), e);
        Assert.Equal(["g"], e.Esgotados);
    }

    [Fact]
    public async Task ParametroAprendidoEhGravado()
    {
        var e = new Estado();
        var r = await Rodar(new Fabrica(new Falso(Provedores.Groq, naoSuporta: ProvedorOpenAI.ParamEsforco), new Falso(Provedores.OpenAI)), e);
        Assert.Equal("g", r.ItemId);
        Assert.Equal([(Provedores.Groq, ProvedorOpenAI.ParamEsforco)], e.Aprendidos);
    }

    [Fact]
    public async Task TodosFalhamLancaComOsPulosParaOServicoResponderComChamado()
    {
        var caiu = new FalhaProvedorException("x", "Erro no servidor do provedor", TipoFalha.Servidor);
        var ex = await Assert.ThrowsAsync<FalhaCadeiaException>(() => Rodar(new Fabrica(new Falso(Provedores.Groq, caiu), new Falso(Provedores.OpenAI, caiu)), new Estado()));
        Assert.Equal(2, ex.Pulos.Count);
        var chamado = AssistenteService.ChamadoQuandoEsgotado("minha pergunta");
        Assert.Equal(("duvida", "Ajuda com o sistema", "minha pergunta"), (chamado.Categoria, chamado.Titulo, chamado.Texto));
    }

    // ------------------------------------------------------------------ alertas na Gestão

    private static readonly ConfigIA CfgAlertas = ConfigIA.Padrao with
    {
        Cadeia = [new ItemCadeia("g", Provedores.Groq, "m-g"), new ItemCadeia("ge", Provedores.Gemini, "m-ge"), new ItemCadeia("o", Provedores.OpenAI, "m-o")],
    };

    private static FotoCadeia FotoCom(params EstadoItem[] e) => new(e.ToDictionary(x => x.ItemId), new Dictionary<string, long>());

    [Fact]
    public void SemProblemaSemAlerta() =>
        Assert.Empty(AlertasCadeia.Calcular(CfgAlertas, FotoCadeia.Vazia, _ => true, Agora, 0, null));

    [Fact]
    public void ItemDesligadoPorErroViraAlertaComDesde()
    {
        var foto = FotoCom(new EstadoItem("g", Provedores.Groq, "m-g", null, true, "Chave recusada", Agora.AddHours(-2)));
        var a = Assert.Single(AlertasCadeia.Calcular(CfgAlertas, foto, _ => true, Agora, 0, null));
        Assert.Equal((AlertasCadeia.ItemDesligado, "g", Agora.AddHours(-2)), (a.Tipo, a.ItemId, a.Desde));
        Assert.Contains("Chave recusada", a.Mensagem);
    }

    [Fact]
    public void TodosOsGratisAcabaramComPagoDisponivel()
    {
        var foto = FotoCom(
            new EstadoItem("g", Provedores.Groq, "m-g", Agora.AddHours(5), false, "Limite", Agora.AddMinutes(-30)),
            new EstadoItem("ge", Provedores.Gemini, "m-ge", Agora.AddHours(5), false, "Limite", Agora.AddMinutes(-10)));
        var a = Assert.Single(AlertasCadeia.Calcular(CfgAlertas, foto, _ => true, Agora, 0, null));
        Assert.Equal((AlertasCadeia.GratisEsgotados, Agora.AddMinutes(-10)), (a.Tipo, a.Desde));
        Assert.Contains("no pago", a.Mensagem);
    }

    [Fact]
    public void SoSemChaveNaoDizQueAcabaram() =>
        Assert.DoesNotContain(AlertasCadeia.Calcular(CfgAlertas, FotoCadeia.Vazia, _ => false, Agora, 0, null), a => a.Tipo == AlertasCadeia.GratisEsgotados);

    [Fact]
    public void UmGratisAindaDisponivelNaoAlerta()
    {
        var foto = FotoCom(new EstadoItem("g", Provedores.Groq, "m-g", Agora.AddHours(5), false, "Limite", Agora));
        Assert.Empty(AlertasCadeia.Calcular(CfgAlertas, foto, _ => true, Agora, 0, null));
    }

    [Fact]
    public void PerguntasSemIaHojeESemChave()
    {
        var alertas = AlertasCadeia.Calcular(CfgAlertas, FotoCadeia.Vazia, d => d.Id != Provedores.OpenAI, Agora, 3, Agora.AddHours(-1));
        Assert.Contains(alertas, a => a.Tipo == AlertasCadeia.CadeiaEsgotada && a.Mensagem.StartsWith("3 perguntas") && a.Desde == Agora.AddHours(-1));
        Assert.Contains(alertas, a => a.Tipo == AlertasCadeia.SemChave && a.ItemId == "o" && a.Mensagem.Contains("OPENAI_API_KEY"));
    }

    [Fact]
    public void MudouUrlOuFlagsEsqueceOAprendido()
    {
        var antes = ConfigIA.Padrao;
        var groq = antes.ProvedorDe(Provedores.Groq)!;
        var depois = antes with { Definicoes = [.. antes.Definicoes.Where(d => d.Id != Provedores.Groq), groq with { AceitaStrict = true }] };
        Assert.Equal([Provedores.Groq], AlertasCadeia.ProvedoresMudados(antes, depois));
        Assert.Empty(AlertasCadeia.ProvedoresMudados(antes, antes));
    }

    // ------------------------------------------------------------------ padrão e edição pela Gestão

    [Fact]
    public void CadeiaPadraoComAsGratisEPagoNoFim()
    {
        var p = ConfigIA.Padrao;
        Assert.Null(p.Problema());
        Assert.Equal(["groq", "gemini", "openrouter", "openai"], p.Cadeia.Select(i => i.Provedor));
        Assert.Equal((ModosCadeia.Ordem, PoliticasPagos.PagoSoNoFim), (p.Modo, p.Pagos));
        Assert.DoesNotContain(p.Cadeia, i => i.Provedor == Provedores.Anthropic);
        var openrouter = p.ItemDe(Provedores.OpenRouter)!;
        Assert.False(openrouter.Ligado);
        Assert.Equal("", openrouter.Modelo);
        Assert.NotNull(p.PrecoDe(p.ItemDe(Provedores.OpenAI)!.Modelo));
    }

    [Fact]
    public void ItemLigadoPrecisaDeModelo()
    {
        var cfg = ConfigIA.Padrao with { Cadeia = [new ItemCadeia("r", Provedores.OpenRouter, "", Ligado: true)] };
        Assert.Contains("Informe o modelo", cfg.Problema());
    }

    [Theory]
    [InlineData("Authorization", "x")]
    [InlineData("X-Api-Key", "x")]
    [InlineData("Content-Type", "x")]
    [InlineData("HTTP-Referer", "linha\nquebrada")]
    [InlineData("nome com espaço", "x")]
    public void CabecalhoProibidoOuInvalido(string nome, string valor) => Assert.NotNull(ConfigIA.ProblemaCabecalho(nome, valor));

    [Fact]
    public void CabecalhoDoOpenRouterValido()
    {
        Assert.Null(ConfigIA.ProblemaCabecalho("HTTP-Referer", "https://zippy.app"));
        var cfg = ConfigIA.Padrao with
        {
            Definicoes = [.. ConfigIA.Padrao.Definicoes.Select(d => d.Id == Provedores.OpenRouter
                ? d with { Cabecalhos = new Dictionary<string, string> { ["Authorization"] = "x" } } : d)],
        };
        Assert.Contains("não pode ser trocado", cfg.Problema());
    }

    [Fact]
    public void ProvedorNovoCriadoPelaGestao()
    {
        var meu = new DefinicaoProvedor("mistral", "Mistral", TiposProvedor.OpenAICompativel, "https://api.exemplo.dev/v1", "ASSISTENTE_MISTRAL_API_KEY", false, false,
            AceitaStrict: true, AceitaEsforco: false, EsquemaCompleto: true);
        var cfg = ConfigIA.Padrao with { Definicoes = [.. ConfigIA.Padrao.Definicoes, meu], Cadeia = [.. ConfigIA.Padrao.Cadeia, new ItemCadeia("m", "mistral", "modelo-x")] };
        Assert.Null(cfg.Problema());
    }
}
