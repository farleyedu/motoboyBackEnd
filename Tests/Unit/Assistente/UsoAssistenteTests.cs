using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Assistente;
using APIBack.Controllers;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class UsoAssistenteTests
{
    private static readonly DateOnly D1 = new(2026, 11, 16);
    private static readonly DateOnly D2 = new(2026, 11, 17);
    private static readonly Guid LojaA = Guid.NewGuid();
    private static readonly Guid LojaB = Guid.NewGuid();
    private static readonly PrecoModelo Opus = new("claude-opus-5-5", 4m, 20m, 0.20m);
    private static readonly ConfigIA SoOpus = ConfigIA.Padrao with { Precos = [Opus] };

    [Fact]
    public void CustoSeparaCacheLidoDaEntrada()
    {
        // 1M de entrada, dos quais 800k vieram do cache: 200k × 4 + 100k × 20 + 800k × 0,20 = 0,8 + 2 + 0,16
        Assert.Equal(2.96m, UsoCalculo.Custo(1_000_000, 100_000, 800_000, Opus));
        Assert.Null(UsoCalculo.Custo(1_000_000, 100_000, 0, null));
    }

    [Fact]
    public void PeriodoPadraoSaoTrintaDiasContandoHoje()
    {
        Assert.Equal((new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 18)), UsoCalculo.Periodo(null, null, new DateOnly(2026, 11, 18)));
        Assert.Equal((D1, D2), UsoCalculo.Periodo("2026-11-16", "2026-11-17", new DateOnly(2026, 11, 18)));
    }

    [Theory]
    [InlineData("2026-11-17", "2026-11-16")]
    [InlineData("16/11/2026", null)]
    [InlineData("2025-01-01", "2026-11-17")]
    public void PeriodoInvalido(string de, string? ate) =>
        Assert.Equal("PERIODO_INVALIDO", Assert.Throws<AssistenteException>(() => UsoCalculo.Periodo(de, ate, D2)).Codigo);

    private static DadosUso Dados() => new(
        PorDia:
        [
            new LinhaUsoDia(D1, "anthropic", "claude-opus-5-5", 10, 0, 1_000_000, 100_000, 800_000, 3, 1),
            new LinhaUsoDia(D2, "anthropic", "claude-opus-5-5", 5, 0, 500_000, 50_000, 400_000, 1, 0),
            new LinhaUsoDia(D2, "openai", "gpt-5.1", 2, 2, 300_000, 10_000, 0, 0, 1),
        ],
        PorLoja:
        [
            new LinhaUsoLoja(LojaA, "Pizzaria A", "claude-opus-5-5", 15, 1_500_000, 150_000, 1_200_000),
            new LinhaUsoLoja(LojaB, "Lanche B", "gpt-5.1", 2, 300_000, 10_000, 0),
        ],
        ConversasPorLoja: new Dictionary<Guid, long> { [LojaA] = 4, [LojaB] = 1 },
        Conversas: 5,
        ErrosPorDia: new Dictionary<DateOnly, long> { [D2] = 3 },
        Limites: [(LojaA, D1), (LojaA, D2), (LojaB, D2)],
        Acoes: new ContagemAcoes(8, 5, 1, 2, 0, 1),
        ChamadosEnviados: 1,
        SemFuncionalidade: 2,
        Ideias: [new Ideia(DateTimeOffset.UnixEpoch, LojaA, "Pizzaria A", "agendar pausa?", "Agendar pausa")]);

    [Fact]
    public void TotaisSomamSoOQueTemPrecoMasTokensDeTodos()
    {
        var u = UsoCalculo.Montar(D1, D2, Dados(), SoOpus);
        Assert.Equal(17, u.Totais.Respostas);
        Assert.Equal(20, u.Totais.Perguntas); // 17 respostas + 3 erros
        Assert.Equal(1_800_000, u.Totais.Tokens.Entrada);
        Assert.Equal(2.96m + 1.48m, u.Totais.CustoUsd);
        Assert.False(u.Totais.CustoCompleto);
        Assert.Equal(2, u.Totais.RespostasDaReserva);
        Assert.Equal(3, u.Totais.BloqueiosPorLimite);
        Assert.Equal(2, u.Totais.LojasNoLimite);
    }

    [Fact]
    public void ModeloSemPrecoVemComCustoNulo()
    {
        var u = UsoCalculo.Montar(D1, D2, Dados(), SoOpus);
        var gpt = u.PorModelo.Single(m => m.Modelo == "gpt-5.1");
        Assert.False(gpt.TemPreco);
        Assert.Null(gpt.CustoUsd);
        Assert.Equal(300_000, gpt.Tokens.Entrada);
        Assert.Equal("claude-opus-5-5", u.PorModelo[0].Modelo);
    }

    [Fact]
    public void PorDiaPreencheDiasVaziosEContaErros()
    {
        var u = UsoCalculo.Montar(D1.AddDays(-1), D2, Dados(), SoOpus);
        Assert.Equal(3, u.PorDia.Count);
        Assert.Equal(0, u.PorDia[0].Perguntas);
        Assert.Equal((10L, 2.96m), (u.PorDia[1].Respostas, u.PorDia[1].CustoUsd));
        Assert.Equal((10L, 7L, 3L), (u.PorDia[2].Perguntas, u.PorDia[2].Respostas, u.PorDia[2].Erros));
    }

    [Fact]
    public void LojasOrdenadasPorCustoComDiasNoLimite()
    {
        var u = UsoCalculo.Montar(D1, D2, Dados(), SoOpus);
        Assert.Equal(["Pizzaria A", "Lanche B"], u.PorLoja.Select(l => l.Loja));
        Assert.Equal((4L, 2L), (u.PorLoja[0].Conversas, u.PorLoja[0].DiasNoLimite));
        Assert.Equal(0m, u.PorLoja[1].CustoUsd);
    }

    [Fact]
    public void ResultadosEIdeiasPassamDireto()
    {
        var u = UsoCalculo.Montar(D1, D2, Dados(), SoOpus);
        Assert.Equal(new AcoesUsoDto(8, 5, 1, 2, 0, 1), u.Resultados.Acoes);
        Assert.Equal((4L, 2L, 1L, 2L), (u.Resultados.TelasAbertas, u.Resultados.RascunhosDeChamado, u.Resultados.ChamadosEnviados, u.Resultados.SemFuncionalidade));
        Assert.Single(u.Ideias);
    }

    [Fact]
    public void SemDadosTudoZeroECustoCompleto()
    {
        var vazio = new DadosUso([], [], new Dictionary<Guid, long>(), 0, new Dictionary<DateOnly, long>(), [], new ContagemAcoes(0, 0, 0, 0, 0, 0), 0, 0, []);
        var u = UsoCalculo.Montar(D2, D2, vazio, ConfigIA.Padrao);
        Assert.True(u.Totais.CustoCompleto);
        Assert.Equal(0m, u.Totais.CustoUsd);
        Assert.Single(u.PorDia);
    }

    // ------------------------------------------------------------------ preços na configuração

    [Fact]
    public void PrecosPadraoAnthropicEOpenAIDaCadeia()
    {
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "gpt-5.4-mini"], ConfigIA.Padrao.Precos.Select(p => p.Modelo));
        Assert.Equal(new PrecoModelo("gpt-5.4-mini", 0.75m, 4.50m, 0.075m), ConfigIA.Padrao.PrecoDe("gpt-5.4-mini"));
        Assert.Equal(new PrecoModelo("claude-haiku-5-5", 0.10m, 0.50m, 0.01m), ConfigIA.Padrao.PrecoDe("claude-haiku-5-5"));
    }

    [Fact]
    public void PrecoRepetidoOuNegativoEhInvalido()
    {
        Assert.Contains("duas vezes", (ConfigIA.Padrao with { Precos = [Opus, Opus with { Entrada = 1 }] }).Problema());
        Assert.Contains("negativo", (ConfigIA.Padrao with { Precos = [Opus with { Saida = -1 }] }).Problema());
        Assert.Null((ConfigIA.Padrao with { Precos = [] }).Problema());
    }

    [Fact]
    public void SemPrecoListaUsadosEConfiguradosSemPreco()
    {
        var cfg = ConfigIA.Padrao with { Cadeia = [new ItemCadeia("a", Provedores.Anthropic, "claude-opus-5-5"), new ItemCadeia("b", Provedores.OpenAI, "gpt-5.1")] };
        // Modelo de provedor grátis (groq) não precisa de preço.
        Assert.Equal(["gpt-4o", "gpt-5.1"], GestaoAssistenteController.SemPreco(cfg,
            [(Provedores.Anthropic, "claude-opus-5-5"), (Provedores.OpenAI, "gpt-4o"), (Provedores.Groq, "modelo-groq")]));
    }

    // ------------------------------------------------------------------ cadeia no uso

    [Fact]
    public void ProvedorGratisCustaZeroComPreco()
    {
        var dados = Dados() with { PorDia = [new LinhaUsoDia(D2, Provedores.Groq, "modelo-groq", 7, 0, 900_000, 9_000, 0, 0, 0)] };
        var u = UsoCalculo.Montar(D2, D2, dados, SoOpus);
        var groq = Assert.Single(u.PorModelo);
        Assert.Equal((0m, true), (groq.CustoUsd, groq.TemPreco));
        Assert.True(u.Totais.CustoCompleto);
    }

    [Fact]
    public void PorItemContaRespostasPulosEEstado()
    {
        var agora = new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
        var cfg = ConfigIA.Padrao with { Cadeia = [new ItemCadeia("g", Provedores.Groq, "modelo-groq", 100), new ItemCadeia("o", Provedores.OpenAI, "gpt-5.1")] };
        var dados = Dados() with
        {
            PorItem = [new LinhaUsoItem("g", Provedores.Groq, "modelo-groq", 30), new LinhaUsoItem("o", Provedores.OpenAI, "gpt-5.1", 4),
                       new LinhaUsoItem("velho", Provedores.Gemini, "m", 2)],
            Pulos = [new LinhaPulo("g", MotivosPulo.Esgotado, 3), new LinhaPulo("g", MotivosPulo.LimiteProvedor, 1)],
        };
        var foto = new FotoCadeia(new Dictionary<string, EstadoItem> { ["g"] = new("g", Provedores.Groq, "modelo-groq", agora.AddMinutes(4), false, "Groq: limite") },
            new Dictionary<string, long>());
        var itens = UsoCalculo.Montar(D1, D2, dados, cfg, foto, agora).PorItem;
        Assert.Equal(["g", "o", "velho"], itens.Select(i => i.ItemId));
        Assert.Equal((30L, true, 3L, 1L), (itens[0].Respostas, itens[0].Gratis, itens[0].Pulos[MotivosPulo.Esgotado], itens[0].Pulos[MotivosPulo.LimiteProvedor]));
        Assert.Equal(agora.AddMinutes(4), itens[0].EsgotadoAte);
        Assert.Equal(0L, itens[1].Pulos[MotivosPulo.SemChave]);
        Assert.False(itens[2].NaCadeia);
    }

    // ------------------------------------------------------------------ o que o chat grava

    [Fact]
    public void ContaCartoesPorTipo()
    {
        List<CartaoDto> cartoes =
        [
            new CartaoAcaoDto(Guid.NewGuid(), "a.b", "t", "r", null, null, DateTimeOffset.UnixEpoch),
            new CartaoTelaDto("a.c", "t", ["p"], "/x"), new CartaoTelaDto("a.d", "t", ["p"], "/y"),
        ];
        Assert.Equal((1, 2, 0), AssistenteService.Contar(cartoes));
    }

    [Fact]
    public void ChamadoLevaAsUltimasMensagensCortadas()
    {
        var msgs = Enumerable.Range(1, 12).Select(i => new MensagemGuardada(Guid.NewGuid(), i % 2 == 1 ? "usuario" : "assistente",
            i == 12 ? new string('x', 400) : $"linha {i}\nsegunda", "[]", DateTimeOffset.UnixEpoch)).ToList();
        var texto = AssistenteService.TextoDoChamado(" Quero agendar ", msgs)!;
        Assert.StartsWith("Quero agendar\n\n— Conversa com o assistente —", texto);
        Assert.DoesNotContain("linha 2 ", texto);   // só as 10 últimas
        Assert.Contains("Loja: linha 3 segunda", texto);
        Assert.Contains("…", texto);
        Assert.Equal("Só texto", AssistenteService.TextoDoChamado("Só texto", []));
    }
}
