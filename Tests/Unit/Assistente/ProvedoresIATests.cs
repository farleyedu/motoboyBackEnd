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

public sealed class ProvedoresIATests
{
    private static readonly IReadOnlyList<JsonObject> Defs = FerramentasAssistente.Definicoes(["cardapio.pausar-produto"]);

    // ------------------------------------------------------------------ tradução das ferramentas

    [Fact]
    public void OpenAIStrictRecebeOMesmoEsquema()
    {
        foreach (var neutra in Defs)
        {
            var t = ProvedorOpenAI.Traduzir(neutra);
            Assert.Equal(neutra["name"]!.GetValue<string>(), t.FunctionName);
            Assert.Equal(neutra["description"]!.GetValue<string>(), t.FunctionDescription);
            Assert.True(t.FunctionSchemaIsStrict);
            Assert.True(JsonNode.DeepEquals(neutra["input_schema"], JsonNode.Parse(t.FunctionParameters.ToString())));
        }
    }

    [Fact]
    public void SemStrictOEsquemaPerdeOsAnulaveis()
    {
        var propor = Defs.Single(d => d["name"]!.GetValue<string>() == FerramentasAssistente.ProporAcao);
        var t = ProvedorOpenAI.Traduzir(propor, strict: false, esquemaCompleto: false);
        Assert.NotEqual(true, t.FunctionSchemaIsStrict);
        var schema = JsonNode.Parse(t.FunctionParameters.ToString())!;
        var parametros = schema["properties"]!["parametros"]!;
        Assert.Equal("string", parametros["properties"]!["data"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", parametros["properties"]!["fechado"]!["type"]!.GetValue<string>());
        Assert.Empty(parametros["required"]!.AsArray());
        Assert.Null(schema["additionalProperties"]);
        Assert.Null(parametros["additionalProperties"]);
        // O que não era anulável continua obrigatório.
        Assert.Contains("funcionalidadeId", schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        // A definição original não muda (cópia).
        Assert.True(propor["strict"]!.GetValue<bool>());
    }

    [Fact]
    public void AnthropicRecebeOMesmoEsquema()
    {
        foreach (var neutra in Defs)
        {
            var t = ProvedorAnthropic.Traduzir(neutra);
            Assert.Equal(neutra["name"]!.GetValue<string>(), t.Name);
            Assert.True(t.Strict);
            Assert.True(JsonNode.DeepEquals(neutra["input_schema"], JsonNode.Parse(JsonSerializer.Serialize(t))!["input_schema"]));
        }
    }

    [Fact]
    public void ArgumentosDaOpenAIViramOMesmoDicionario()
    {
        var args = ProvedorOpenAI.LerArgumentos("""{"tipo":"produto","busca":"x-bacon"}""")!;
        Assert.Equal(new FerramentasAssistente.PedidoConsulta("produto", "x-bacon"), FerramentasAssistente.LerConsulta(args));
        Assert.Null(ProvedorOpenAI.LerArgumentos("{\"tipo\":"));
        Assert.Empty(ProvedorOpenAI.LerArgumentos("")!);
    }

    [Fact]
    public void FallbackNoServidorSoNosModelosQueAceitam()
    {
        Assert.True(ProvedorAnthropic.UsaFallbackNoServidor("claude-opus-5-5"));
        Assert.False(ProvedorAnthropic.UsaFallbackNoServidor("claude-haiku-5-5"));
    }

    [Fact]
    public void ListaDaOpenAISoComModelosDeConversa() =>
        Assert.Equal(["gpt-5.1", "o4-mini"],
            ProvedorOpenAI.FiltrarModelosChat(["text-embedding-3-large", "o4-mini", "whisper-1", "gpt-5.1", "dall-e-3", "gpt-4o-realtime-preview", "gpt-5.1"]));

    // ------------------------------------------------------------------ classificação das falhas

    [Fact]
    public void RedeEhServidorEOutroErroEhPedido()
    {
        Assert.Equal(TipoFalha.Servidor, ProvedorOpenAI.Classificar(new System.Net.Http.HttpRequestException("x")).Tipo);
        Assert.Equal(TipoFalha.Servidor, ProvedorAnthropic.Classificar(new TaskCanceledException()).Tipo);
        Assert.Equal(TipoFalha.Pedido, ProvedorAnthropic.Classificar(new InvalidOperationException("x")).Tipo);
    }

    [Theory]
    [InlineData("Rate limit reached for requests per day (RPD): Limit 1000", true)]
    [InlineData("You exceeded your current quota, please check your plan", true)]
    [InlineData("Rate limit exceeded: free-models-per-day", true)]
    [InlineData("Rate limit reached for requests per minute", false)]
    [InlineData(null, false)]
    public void CotaDoDia(string? mensagem, bool diaria) => Assert.Equal(diaria, ProvedorOpenAI.CotaDiaria(mensagem));

    [Fact]
    public void RetryAfterEmSegundosOuData()
    {
        var agora = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(30), ProvedorOpenAI.LerRetryAfter("30", agora));
        Assert.Equal(TimeSpan.FromMinutes(2), ProvedorOpenAI.LerRetryAfter("Wed, 18 Nov 2026 12:02:00 GMT", agora));
        Assert.Null(ProvedorOpenAI.LerRetryAfter("amanhã", agora));
    }

    // ------------------------------------------------------------------ esgotado até

    private static readonly DateTimeOffset Agora = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero); // 12:00 em São Paulo

    [Fact]
    public void EsgotadoAtePeloRetryAfterMeiaNoiteOuCincoMinutos()
    {
        Assert.Equal(Agora.AddSeconds(40), RegrasCadeia.EsgotadoAte(new FalhaProvedorException("groq", "x", TipoFalha.Limite, retryAfter: TimeSpan.FromSeconds(40)), Agora));
        // Cota do dia: até a meia-noite de São Paulo (03:00 UTC do dia seguinte).
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero),
            RegrasCadeia.EsgotadoAte(new FalhaProvedorException("groq", "x", TipoFalha.Limite, cotaDiaria: true), Agora));
        Assert.Equal(Agora.AddMinutes(5), RegrasCadeia.EsgotadoAte(new FalhaProvedorException("groq", "x", TipoFalha.Servidor), Agora));
        Assert.Equal(Agora.AddHours(24), RegrasCadeia.EsgotadoAte(new FalhaProvedorException("groq", "x", TipoFalha.Limite, retryAfter: TimeSpan.FromDays(3)), Agora));
    }

    // ------------------------------------------------------------------ plano da cadeia

    private static readonly ItemCadeia Groq = new("g", Provedores.Groq, "modelo-g", 100);
    private static readonly ItemCadeia Router = new("r", Provedores.OpenRouter, "modelo-r:free", 50);
    private static readonly ItemCadeia OpenAI = new("o", Provedores.OpenAI, "gpt-5.1");

    private static ConfigIA Cfg(string modo = ModosCadeia.Ordem, string pagos = PoliticasPagos.PagoSoNoFim, params ItemCadeia[] itens) =>
        ConfigIA.Padrao with { Modo = modo, Pagos = pagos, Cadeia = itens.Length == 0 ? [OpenAI, Groq, Router] : itens };

    private static FotoCadeia Foto(Dictionary<string, long>? usadas = null, params EstadoItem[] estados) =>
        new(estados.ToDictionary(e => e.ItemId), usadas ?? new Dictionary<string, long>());

    private static bool TodasAsChaves(DefinicaoProvedor _) => true;

    private static IEnumerable<string> Ids(PlanoCadeia p) => p.Tentar.Select(i => i.Id);

    [Fact]
    public void OrdemComPagoSoNoFimPoeGratisPrimeiro()
    {
        var plano = RegrasCadeia.Planejar(Cfg(), Foto(), TodasAsChaves, Agora);
        Assert.Equal(["g", "r", "o"], Ids(plano));
        Assert.Empty(plano.Pulados);
    }

    [Fact]
    public void NuncaPagoTiraOPago()
    {
        var plano = RegrasCadeia.Planejar(Cfg(pagos: PoliticasPagos.NuncaPago), Foto(), TodasAsChaves, Agora);
        Assert.Equal(["g", "r"], Ids(plano));
        Assert.Equal((OpenAI, MotivosPulo.PagoBloqueado), (plano.Pulados.Single().Item, plano.Pulados.Single().Motivo));
    }

    [Fact]
    public void EsgotadoLimiteDoDiaSemChaveEDesligadoFicamDeFora()
    {
        var cfg = Cfg(itens: [Groq, Router, OpenAI, new ItemCadeia("off", Provedores.Gemini, "m", Ligado: false)]);
        var foto = Foto(new Dictionary<string, long> { ["r"] = 50 },
            new EstadoItem("g", Provedores.Groq, "modelo-g", Agora.AddMinutes(3), false, "Groq: limite"));
        var plano = RegrasCadeia.Planejar(cfg, foto, d => d.Id != Provedores.OpenAI, Agora);
        Assert.Empty(plano.Tentar);
        Assert.Equal([("g", MotivosPulo.Esgotado), ("r", MotivosPulo.LimiteDia), ("o", MotivosPulo.SemChave)],
            plano.Pulados.Select(p => (p.Item.Id, p.Motivo)));
    }

    [Fact]
    public void EsgotadoQueJaPassouVoltaADisponivel()
    {
        var foto = Foto(null, new EstadoItem("g", Provedores.Groq, "modelo-g", Agora.AddSeconds(-1), false, null));
        Assert.Equal("g", RegrasCadeia.Planejar(Cfg(), foto, TodasAsChaves, Agora).Tentar[0].Id);
    }

    [Fact]
    public void DesligadoPorErroSoValeParaOMesmoModelo()
    {
        var desligado = new EstadoItem("g", Provedores.Groq, "modelo-g", null, true, "Groq: modelo não encontrado (404).");
        Assert.Equal(MotivosPulo.DesligadoErro, RegrasCadeia.Planejar(Cfg(), Foto(null, desligado), TodasAsChaves, Agora).Pulados.Single().Motivo);
        // A Gestão trocou o modelo do item: o estado antigo não vale mais.
        var trocado = Cfg(itens: [Groq with { Modelo = "outro" }]);
        Assert.Equal(["g"], Ids(RegrasCadeia.Planejar(trocado, Foto(null, desligado), TodasAsChaves, Agora)));
    }

    [Fact]
    public void RevezarMandaParaQuemGastouMenosDoLimite()
    {
        // g usou 60/100 (60%), r usou 10/50 (20%): r primeiro. O pago continua no fim.
        var plano = RegrasCadeia.Planejar(Cfg(ModosCadeia.Revezar), Foto(new Dictionary<string, long> { ["g"] = 60, ["r"] = 10, ["o"] = 0 }), TodasAsChaves, Agora);
        Assert.Equal(["r", "g", "o"], Ids(plano));
    }

    [Fact]
    public void RevezarSemLimiteContaComoMil()
    {
        var semLimite = new ItemCadeia("s", Provedores.Gemini, "m");
        // s: 300/1000 = 30%; g: 40/100 = 40%.
        var plano = RegrasCadeia.Planejar(Cfg(ModosCadeia.Revezar, itens: [Groq, semLimite]), Foto(new Dictionary<string, long> { ["g"] = 40, ["s"] = 300 }), TodasAsChaves, Agora);
        Assert.Equal(["s", "g"], Ids(plano));
    }

    [Fact]
    public void RevezarEmpatadoFicaNaOrdemDaCadeia() =>
        Assert.Equal(["g", "r"], Ids(RegrasCadeia.Planejar(Cfg(ModosCadeia.Revezar, itens: [Groq, Router]), Foto(), TodasAsChaves, Agora)));

    // ------------------------------------------------------------------ troca automática na mesma pergunta

    private sealed class Falso(string id, Exception? falha = null, string? naoSuporta = null) : IProvedorIA
    {
        public readonly List<string> Modelos_ = [];
        public string Nome => id;
        public bool TemChave => true;
        public Task<RespostaIA> Responder(PedidoIA pedido, ExecutarFerramenta executar, CancellationToken ct)
        {
            Modelos_.Add(pedido.Modelo);
            if (naoSuporta is not null) pedido.NaoSuporta?.Invoke(naoSuporta);
            return falha is null ? Task.FromResult(new RespostaIA($"{id}:{pedido.Modelo}", DesfechoIA.Normal, 1, 1, 0)) : Task.FromException<RespostaIA>(falha);
        }
        public Task<IReadOnlyList<string>> Modelos(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class Fabrica(params Falso[] provedores) : IFabricaProvedores
    {
        public readonly HashSet<string> SemChave = [];
        public IProvedorIA Criar(DefinicaoProvedor d) => provedores.First(p => p.Nome == d.Id);
        public bool TemChave(DefinicaoProvedor d) => !SemChave.Contains(d.Id);
    }

    private sealed class EstadoFalso : IEstadoCadeiaRepositorio
    {
        public FotoCadeia FotoAtual = new(new Dictionary<string, EstadoItem>(), new Dictionary<string, long>());
        public readonly List<(string Item, DateTimeOffset Ate)> Esgotados = [];
        public readonly List<string> Desligados = [];
        public Task<FotoCadeia> Foto(DateTimeOffset inicioDoDiaUtc, CancellationToken ct) => Task.FromResult(FotoAtual);
        public Task MarcarEsgotado(ItemCadeia item, DateTimeOffset ate, string erro, CancellationToken ct) { Esgotados.Add((item.Id, ate)); return Task.CompletedTask; }
        public Task Desligar(ItemCadeia item, string erro, CancellationToken ct) { Desligados.Add(item.Id); return Task.CompletedTask; }
        public Task Religar(string itemId, CancellationToken ct) => Task.CompletedTask;
        public readonly List<(string Provedor, string Parametro)> Aprendidos = [];
        public Task RegistrarNaoSuporta(string provedor, string parametro, CancellationToken ct) { Aprendidos.Add((provedor, parametro)); return Task.CompletedTask; }
        public Task LimparNaoSuporta(IReadOnlyCollection<string> provedores, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Relogio(DateTimeOffset agora) : TimeProvider { public override DateTimeOffset GetUtcNow() => agora; }

    private static CadeiaIA Cadeia(IFabricaProvedores f, EstadoFalso e) => new(f, e, new Relogio(Agora), NullLogger<CadeiaIA>.Instance);
    private static PedidoIA Pedido(ItemCadeia i) => new(i.Modelo, "low", 1000, "s", Defs, [], "c", "t");
    private static Task<ResultadoFerramenta> Nada(string n, IReadOnlyDictionary<string, JsonElement> i, CancellationToken c) => Task.FromResult(new ResultadoFerramenta("", false));

    [Fact]
    public async Task UsaOPrimeiroQueResponde()
    {
        var o = new Falso(Provedores.OpenAI);
        var r = await Cadeia(new Fabrica(new Falso(Provedores.Groq), new Falso(Provedores.OpenRouter), o), new EstadoFalso())
            .Responder(Cfg(), Pedido, Nada, null, default);
        Assert.Equal(("g", false), (r.ItemId, r.Trocou));
        Assert.Empty(o.Modelos_);
    }

    [Fact]
    public async Task LimiteNoProvedorPassaAoProximoNaMesmaPerguntaEMarcaEsgotado()
    {
        var limpou = 0;
        var estado = new EstadoFalso();
        var limite = new FalhaProvedorException(Provedores.Groq, "Groq: limite de uso atingido.", TipoFalha.Limite, retryAfter: TimeSpan.FromSeconds(20));
        var r = await Cadeia(new Fabrica(new Falso(Provedores.Groq, limite), new Falso(Provedores.OpenRouter), new Falso(Provedores.OpenAI)), estado)
            .Responder(Cfg(), Pedido, Nada, () => limpou++, default);
        Assert.Equal(("r", true), (r.ItemId, r.Trocou));
        Assert.Equal([("g", Agora.AddSeconds(20))], estado.Esgotados);
        Assert.Equal(1, limpou);
        Assert.Equal(("g", MotivosPulo.LimiteProvedor), (r.Pulos.Single().Item.Id, r.Pulos.Single().Motivo));
    }

    [Fact]
    public async Task ModeloInexistenteDesligaOItem()
    {
        var estado = new EstadoFalso();
        var naoExiste = new FalhaProvedorException(Provedores.Groq, "Groq: modelo não encontrado (404).", TipoFalha.Configuracao);
        var r = await Cadeia(new Fabrica(new Falso(Provedores.Groq, naoExiste), new Falso(Provedores.OpenRouter), new Falso(Provedores.OpenAI)), estado)
            .Responder(Cfg(), Pedido, Nada, null, default);
        Assert.Equal("r", r.ItemId);
        Assert.Equal(["g"], estado.Desligados);
        Assert.Empty(estado.Esgotados);
    }

    [Fact]
    public async Task PedidoRecusadoPassaSemMarcarNada()
    {
        var estado = new EstadoFalso();
        var recusado = new FalhaProvedorException(Provedores.Groq, "Groq: pedido recusado (400).", TipoFalha.Pedido);
        var r = await Cadeia(new Fabrica(new Falso(Provedores.Groq, recusado), new Falso(Provedores.OpenRouter), new Falso(Provedores.OpenAI)), estado)
            .Responder(Cfg(), Pedido, Nada, null, default);
        Assert.Equal("r", r.ItemId);
        Assert.Empty(estado.Esgotados);
        Assert.Empty(estado.Desligados);
    }

    [Fact]
    public async Task ItemSemChaveEhPuladoNoPlano()
    {
        var fabrica = new Fabrica(new Falso(Provedores.Groq), new Falso(Provedores.OpenRouter), new Falso(Provedores.OpenAI));
        fabrica.SemChave.Add(Provedores.Groq);
        var r = await Cadeia(fabrica, new EstadoFalso()).Responder(Cfg(), Pedido, Nada, null, default);
        Assert.Equal(("r", false), (r.ItemId, r.Trocou));
        Assert.Equal(MotivosPulo.SemChave, r.Pulos.Single().Motivo);
    }

    [Fact]
    public async Task TodosFalhamLevaOsPulos()
    {
        var caiu = new FalhaProvedorException("x", "caiu", TipoFalha.Servidor);
        var ex = await Assert.ThrowsAsync<FalhaCadeiaException>(() =>
            Cadeia(new Fabrica(new Falso(Provedores.Groq, caiu), new Falso(Provedores.OpenRouter, caiu), new Falso(Provedores.OpenAI, caiu)), new EstadoFalso())
                .Responder(Cfg(), Pedido, Nada, null, default));
        Assert.Equal(3, ex.Pulos.Count);
        Assert.All(ex.Pulos, p => Assert.Equal(MotivosPulo.ErroServidor, p.Motivo));
    }

    [Fact]
    public async Task NadaDisponivelFalhaSemChamarNinguem()
    {
        var g = new Falso(Provedores.Groq);
        var estado = new EstadoFalso { FotoAtual = Foto(null, new EstadoItem("g", Provedores.Groq, "modelo-g", Agora.AddHours(1), false, null)) };
        var ex = await Assert.ThrowsAsync<FalhaCadeiaException>(() =>
            Cadeia(new Fabrica(g), estado).Responder(Cfg(itens: [Groq]), Pedido, Nada, null, default));
        Assert.Contains("Nenhuma IA", ex.Message);
        Assert.Empty(g.Modelos_);
    }

    [Fact]
    public void DisponivelPrecisaEstarLigadoEComAlgumItemUtil()
    {
        var fabrica = new Fabrica();
        fabrica.SemChave.Add(Provedores.Anthropic);
        var cadeia = Cadeia(fabrica, new EstadoFalso());
        Assert.False(cadeia.Disponivel(Cfg(itens: [new ItemCadeia("a", Provedores.Anthropic, "claude-opus-5-5")]))); // só Anthropic, sem chave
        Assert.True(cadeia.Disponivel(Cfg()));
        Assert.False(cadeia.Disponivel(Cfg() with { Ligado = false }));
        Assert.False(cadeia.Disponivel(Cfg(pagos: PoliticasPagos.NuncaPago, itens: [OpenAI])));
    }

    // ------------------------------------------------------------------ configuração

    [Fact]
    public void FormatoAntigoViraCadeiaDeDoisItens()
    {
        var n = (ConfigIA.Padrao with { Provedor = "openai", Modelo = " gpt-5.1 ", ReservaProvedor = "anthropic", ReservaModelo = "claude-opus-5-5" }).Normalizada();
        Assert.Equal([new ItemCadeia("principal", "openai", "gpt-5.1"), new ItemCadeia("reserva", "anthropic", "claude-opus-5-5")], n.Cadeia);
        Assert.Null(n.Provedor);
        Assert.Null(n.Problema());
    }

    [Fact]
    public void PadraoEhValido()
    {
        Assert.Null(ConfigIA.Padrao.Problema());
        Assert.Equal(["groq", "gemini", "openrouter", "openai", "anthropic"], ConfigIA.Padrao.Definicoes.Select(d => d.Id));
        Assert.True(ConfigIA.Padrao.ProvedorDe("gemini")!.UsaDadosParaTreinar);
    }

    public static IEnumerable<object[]> Invalidas()
    {
        var p = ConfigIA.Padrao;
        yield return [p with { Cadeia = [] }, "1 a 10"];
        yield return [p with { Cadeia = [new ItemCadeia("a", "nao-existe", "m")] }, "não está cadastrado"];
        yield return [p with { Cadeia = [new ItemCadeia("a", "groq", "m"), new ItemCadeia("a", "groq", "n")] }, "duas vezes"];
        yield return [p with { Cadeia = [new ItemCadeia("a", "groq", "m", Ligado: false)] }, "ligado"];
        yield return [p with { Cadeia = [new ItemCadeia("a", "groq", "m", LimiteDia: 0)] }, "Limite do dia"];
        yield return [p with { Modo = "aleatorio" }, "ordem ou revezar"];
        yield return [p with { Pagos = "sempre" }, "pagoSoNoFim"];
        yield return [p with { Definicoes = [.. p.Definicoes, new DefinicaoProvedor("meu", "Meu", "openai", "http://inseguro", "ASSISTENTE_MEU_API_KEY", true, false, false)] }, "https://"];
        yield return [p with { Definicoes = [.. p.Definicoes, new DefinicaoProvedor("meu", "Meu", "openai", "https://x.dev/v1", "DATABASE_URL", true, false, false)] }, "Variável"];
    }

    [Theory]
    [MemberData(nameof(Invalidas))]
    public void ConfigInvalida(ConfigIA cfg, string trecho) => Assert.Contains(trecho, cfg.Problema(), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("GROQ_API_KEY", true)]
    [InlineData("ASSISTENTE_MISTRAL_API_KEY", true)]
    [InlineData("DATABASE_URL", false)]
    [InlineData("JWT_SECRET", false)]
    [InlineData("ASSISTENTE_API_KEY", false)]
    public void SoLeVariaveisPermitidas(string nome, bool ok) => Assert.Equal(ok, ConfigIA.VariavelPermitida(nome));
}

public sealed class ParametrosAnthropicTests
{
    [Fact]
    public void WithNosParametrosDoSdkMantemOsCampos()
    {
        // O provedor monta os parâmetros e acrescenta esforço/fallback com "with": a cópia não pode perder modelo nem mensagens.
        var p = new Anthropic.Models.Beta.Messages.MessageCreateParams
        {
            Model = "claude-opus-5-5", MaxTokens = 123,
            Messages = [new Anthropic.Models.Beta.Messages.BetaUserMessageParam { Content = "oi" }],
        };
        var q = p with { OutputConfig = new Anthropic.Models.Beta.Messages.BetaOutputConfig { Effort = Anthropic.Models.Beta.Messages.Effort.Low } };
        q = q with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Anthropic.Models.Beta.Messages.Default() };
        Assert.Equal(123, q.MaxTokens);
        Assert.Single(q.Messages);
        Assert.NotNull(q.OutputConfig);
        Assert.NotNull(q.Fallbacks);
        Assert.Contains("claude-opus-5-5", q.Model.ToString());
    }
}

public sealed class ChecagemFerramentasTests
{
    private static AssistenteService.ChamadaTeste C(string nome, string? erro = null) => new(nome, erro);

    [Fact]
    public void ChamouAEsperadaComArgumentosValidos() =>
        Assert.True(AssistenteService.AvaliarChecagem("consultar", "p", [C("consultar")], null, 10).Ok);

    [Fact]
    public void NaoChamouChamouOutraOuArgumentosRuins()
    {
        Assert.Contains("sem chamar", AssistenteService.AvaliarChecagem("propor_acao", "p", [], null, 1).Erro);
        Assert.Contains("em vez de propor_acao", AssistenteService.AvaliarChecagem("propor_acao", "p", [C("abrir_tela")], null, 1).Erro);
        Assert.Contains("Argumentos inválidos", AssistenteService.AvaliarChecagem("abrir_tela", "p", [C("abrir_tela", "funcionalidadeId é obrigatório")], null, 1).Erro);
        Assert.Equal("Groq: limite", AssistenteService.AvaliarChecagem("consultar", "p", [], "Groq: limite", 1).Erro);
    }

    [Fact]
    public void ValidaArgumentosComALeituraDoChat()
    {
        System.Collections.Generic.IReadOnlyDictionary<string, System.Text.Json.JsonElement> Args(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(json)!;
        Assert.Null(AssistenteService.ValidarArgumentos("consultar", Args("""{"tipo":"loja_status"}""")));
        Assert.NotNull(AssistenteService.ValidarArgumentos("consultar", Args("""{"tipo":"outra"}""")));
        Assert.NotNull(AssistenteService.ValidarArgumentos("abrir_tela", Args("{}")));
        Assert.Equal(4, AssistenteService.Checagens.Select(c => c.Ferramenta).Distinct().Count());
    }
}
