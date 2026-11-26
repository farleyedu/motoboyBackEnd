using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Assistente;
using APIBack.Controllers;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

/// <summary>CONTRATO-API §12: reconhecer a falha pelo status e pelo corpo, sem vazar chave, e resumir para a Gestão.</summary>
public sealed class DiagnosticoIATests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ status + corpo

    [Theory]
    // Gemini responde 400 (não 401) para chave ruim.
    [InlineData(400, """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]""", CodigosFalha.ChaveRecusada)]
    // Cota grátis do Gemini: a frase fala em "quota" e "billing", mas NÃO é falta de crédito.
    [InlineData(429, """[{"error":{"code":429,"message":"You exceeded your current quota, please check your plan and billing details.","status":"RESOURCE_EXHAUSTED","details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}]""", CodigosFalha.CotaDia)]
    [InlineData(429, """[{"error":{"code":429,"message":"You exceeded your current quota, please check your plan and billing details.","status":"RESOURCE_EXHAUSTED","details":[{"violations":[{"quotaId":"GenerateRequestsPerMinutePerProjectPerModel-FreeTier"}]}]}}]""", CodigosFalha.LimiteMinuto)]
    [InlineData(429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""", CodigosFalha.SemCredito)]
    [InlineData(429, """{"error":{"message":"Rate limit reached on tokens per day (TPD)"}}""", CodigosFalha.CotaDia)]
    [InlineData(429, """{"error":{"message":"Rate limit exceeded: free-models-per-day"}}""", CodigosFalha.CotaDia)]
    [InlineData(429, """{"error":{"message":"Rate limit reached for requests per minute"}}""", CodigosFalha.LimiteMinuto)]
    [InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low"}}""", CodigosFalha.SemCredito)]
    [InlineData(404, """{"error":{"message":"The model `x` does not exist or you do not have access to it.","code":"model_not_found"}}""", CodigosFalha.ModeloSemAcesso)]
    [InlineData(404, """{"error":{"message":"No endpoints found that support tool use."}}""", CodigosFalha.ModeloSemFerramentas)]
    [InlineData(400, """{"error":{"message":"x is not a valid model ID"}}""", CodigosFalha.ModeloNaoExiste)]
    [InlineData(400, """[{"error":{"message":"User location is not supported for the API use.","status":"FAILED_PRECONDITION"}}]""", CodigosFalha.RegiaoBloqueada)]
    [InlineData(400, """{"error":{"message":"This model's maximum context length is 8192 tokens"}}""", CodigosFalha.PedidoGrande)]
    [InlineData(403, """[{"error":{"message":"Generative Language API has not been used in project 1","status":"PERMISSION_DENIED"}}]""", CodigosFalha.SemPermissao)]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", CodigosFalha.ModeloSobrecarregado)]
    [InlineData(502, "Bad gateway", CodigosFalha.ErroInternoProvedor)]
    [InlineData(200, "<!DOCTYPE html><html></html>", CodigosFalha.UrlErrada)]
    public void StatusECorpoViramCodigo(int status, string corpo, string codigo) =>
        Assert.Equal(codigo, ProvedorOpenAI.PorStatus("gemini", status, corpo, etapa: EtapasFalha.ChamadaModelo).Codigo);

    [Fact]
    public void Quatrocentos404NaListaEhEnderecoErrado() =>
        Assert.Equal(CodigosFalha.UrlErrada, ProvedorOpenAI.PorStatus("meu", 404, "Not Found", etapa: EtapasFalha.ListaModelos).Codigo);

    [Fact]
    public void EsperaVemDoCorpoDoGemini() =>
        Assert.Equal(TimeSpan.FromSeconds(37), CorpoErro.Espera("""{"details":[{"retryDelay": "37s"}]}"""));

    [Fact]
    public void LimitePorMinutoSemEsperaDuraUmMinutoEOpenRouterZeraMeiaNoiteUtc()
    {
        Assert.Equal(Agora.AddMinutes(1), RegrasCadeia.EsgotadoAte(ProvedorOpenAI.PorStatus("groq", 429, "per minute"), Agora));
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero),
            RegrasCadeia.EsgotadoAte(ProvedorOpenAI.PorStatus("openrouter", 429, "free-models-per-day"), Agora));
    }

    [Fact]
    public void TodoCodigoTemTituloCurtoEDetalhe() => Assert.All(CatalogoFalhas.Codigos, c =>
    {
        Assert.InRange(CatalogoFalhas.De(c).Titulo.Length, 1, 80);
        Assert.NotEmpty(CatalogoFalhas.De(c).Acoes);
        Assert.NotEmpty(CatalogoFalhas.Detalhe(c, EtapasFalha.ChamadaModelo, "Gemini", "m", 500));
    });

    // ------------------------------------------------------------------ chave nunca sai

    [Theory]
    [InlineData("chave AQ.Ab8RN6abcdefghijklmnop recusada", "AQ.Ab8")]
    [InlineData("Incorrect key gsk_abcdefghijkl", "gsk_abc")]
    [InlineData("GET /models?key=segredo123&x=1", "segredo123")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "abcdefgh12345678")]
    public void ChavesConhecidasSaoApagadas(string texto, string segredo) => Assert.DoesNotContain(segredo, Redacao.Limpar(texto));

    [Fact]
    public void ChaveDeFormatoDesconhecidoSaiAntesDeCortar()
    {
        const string chave = "minha-chave-propria-123456";
        var f = ProvedorOpenAI.Classificar(new InvalidOperationException(new string('x', 290) + chave), "meu", chave: chave);
        Assert.DoesNotContain("minha", f.MensagemProvedor);
    }

    // ------------------------------------------------------------------ modelos

    [Theory]
    [InlineData("gemini-2.5-flash", "conversa")]
    [InlineData("gemini-3.8-flash-tts", "voz")]
    [InlineData("gemini-3.8-live", "voz")]
    [InlineData("gemini-3-pro-image", "imagem")]
    [InlineData("gemini-embedding-2", "embedding")]
    [InlineData("lyria-3.5", "musica")]
    [InlineData("genie-4", "video")]
    [InlineData("deep-research-preview-04-2026", "outro")]
    public void TipoDoModelo(string id, string tipo) => Assert.Equal(tipo, CatalogoModelos.Tipo(id));

    [Fact]
    public void ListaTiraPrefixoEPoeRecomendadosNaFrente()
    {
        var lista = CatalogoModelos.Montar("gemini", ["models/gemini-3.8-flash-tts", "models/gemini-3-flash-preview", "models/gemini-2.5-flash"]);
        Assert.Equal(["gemini-2.5-flash", "gemini-3-flash-preview", "gemini-3.8-flash-tts"], lista.Select(m => m.Id));
        Assert.True(lista[0].Recomendado);
        Assert.False(lista[1].Estavel);
        Assert.Equal(["gemini-2.5-flash"], CatalogoModelos.Sugeridos("gemini", lista.Select(m => m.Id), "gemini-3.8-flash"));
    }

    // ------------------------------------------------------------------ salvar chave

    [Theory]
    [InlineData(true, SituacoesEtapa.Pulada, true, true)]   // OpenRouter sem item com modelo: só o formato
    [InlineData(true, SituacoesEtapa.Falhou, false, true)]  // chamada falhou por outro motivo que não a chave
    [InlineData(true, SituacoesEtapa.Pulada, false, false)] // parou antes da chamada: não provou
    [InlineData(false, SituacoesEtapa.Pulada, false, true)] // lista fechada aceitou a chave
    public void QuandoAChaveEstaProvada(bool listaPublica, string chamada, bool ok, bool provada)
    {
        var d = new DiagnosticoDto("openrouter", null, null, ok,
        [
            new(EtapasFalha.ListaModelos, listaPublica ? SituacoesEtapa.Aviso : SituacoesEtapa.Ok, 1, "lista", null),
            new(EtapasFalha.ChamadaModelo, chamada, null, "chamada", null),
        ], null, null, listaPublica);
        Assert.Equal(provada, GestaoChavesIA.Provada(d));
    }

    // ------------------------------------------------------------------ estado e saúde

    private static (ConfigIA Cfg, FotoCadeia Foto) ComFalhaGuardada(DateTimeOffset esgotadoAte)
    {
        var cfg = ConfigIA.Padrao with { Cadeia = [new ItemCadeia("g", "gemini", "gemini-3.8-flash")] };
        var falha = CatalogoFalhas.Dto(CatalogoFalhas.Nova("gemini", CodigosFalha.ModeloSobrecarregado, 503), "Gemini", "gemini-3.8-flash", Agora);
        var estado = new EstadoItem("g", "gemini", "gemini-3.8-flash", esgotadoAte, false, falha.Titulo, Agora, falha, Agora);
        return (cfg, new FotoCadeia(new Dictionary<string, EstadoItem> { ["g"] = estado }, new Dictionary<string, long>()));
    }

    private static EstadoItemDto Estado(ConfigIA cfg, FotoCadeia foto) => GestaoAssistenteController.EstadoComFalha(cfg,
        RegrasCadeia.Disponibilidade(cfg, cfg.Cadeia[0], foto, _ => true, Agora), foto,
        new Dictionary<string, IReadOnlyList<string>> { ["gemini"] = ["gemini-2.5-flash"] });

    [Fact]
    public void ItemParadoMostraAFalhaComModelosParaTrocar()
    {
        var (cfg, foto) = ComFalhaGuardada(Agora.AddMinutes(5));
        var e = Estado(cfg, foto);
        Assert.Equal(CodigosFalha.ModeloSobrecarregado, e.Falha!.Codigo);
        Assert.Equal(["gemini-2.5-flash"], e.Falha.ModelosSugeridos);
        var saude = GestaoAssistenteController.Saude(cfg, [e]);
        Assert.Equal(("parado", "Nenhuma IA respondendo"), (saude.Situacao, saude.Titulo));
    }

    [Fact]
    public void ItemQueVoltouNaoMostraFalhaVelha()
    {
        var (cfg, foto) = ComFalhaGuardada(Agora.AddMinutes(-1));
        var e = Estado(cfg, foto);
        Assert.True(e.Disponivel);
        Assert.Null(e.Falha);
        Assert.Null(e.UltimaFalhaEm);
        Assert.Equal("ok", GestaoAssistenteController.Saude(cfg, [e]).Situacao);
    }
}
