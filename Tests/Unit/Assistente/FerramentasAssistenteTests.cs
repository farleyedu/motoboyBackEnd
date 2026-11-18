using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Assistente;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class FerramentasAssistenteTests
{
    private static IReadOnlyDictionary<string, JsonElement> Input(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>strict: todo objeto fecha additionalProperties e exige todas as propriedades.</summary>
    private static void ConferirStrict(JsonObject schema)
    {
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var props = schema["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k);
        var exigidas = schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).OrderBy(k => k);
        Assert.Equal(props, exigidas);
        foreach (var p in schema["properties"]!.AsObject())
            if (p.Value is JsonObject filho && filho["type"]?.ToString() == "object") ConferirStrict(filho);
    }

    [Fact]
    public void QuatroFerramentasStrict()
    {
        var defs = FerramentasAssistente.Definicoes(["cardapio.pausar-produto"]);
        Assert.Equal([FerramentasAssistente.Consultar, FerramentasAssistente.ProporAcao, FerramentasAssistente.AbrirTela, FerramentasAssistente.RascunhoChamado],
            defs.Select(d => d["name"]!.GetValue<string>()));
        Assert.All(defs, d =>
        {
            Assert.True(d["strict"]!.GetValue<bool>());
            ConferirStrict(d["input_schema"]!.AsObject());
        });
        var enumAcoes = defs[1]["input_schema"]!["properties"]!["funcionalidadeId"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Equal(["cardapio.pausar-produto"], enumAcoes);
    }

    [Fact]
    public void SemAcaoComExecutorNaoOferecePropor()
    {
        Assert.DoesNotContain(FerramentasAssistente.Definicoes([]), d => d["name"]!.GetValue<string>() == FerramentasAssistente.ProporAcao);
    }

    [Fact]
    public void LeParametrosDaAcao()
    {
        var p = FerramentasAssistente.LerAcao(Input("""
            { "funcionalidadeId": "loja.feriado", "resumo": "Fechar sexta",
              "parametros": { "produto": null, "data": "2026-11-20", "fechado": true, "abreAs": null, "fechaAs": null, "motivo": "Consciência Negra" } }
            """));
        Assert.Equal("loja.feriado", p.FuncionalidadeId);
        Assert.Equal(new ParametrosAcao(null, "2026-11-20", true, null, null, "Consciência Negra"), p.Parametros);
    }

    [Fact]
    public void ChamadoComCategoriaDesconhecidaViraIdeiaETituloCurto()
    {
        var c = FerramentasAssistente.LerChamado(Input($$"""{ "categoria": "outra", "titulo": "{{new string('a', 120)}}", "texto": "Quero agendar." }"""));
        Assert.Equal("ideia", c.Categoria);
        Assert.Equal(80, c.Titulo.Length);
    }

    [Fact]
    public void ConsultaForaDaListaFalha()
    {
        Assert.Throws<System.ArgumentException>(() => FerramentasAssistente.LerConsulta(Input("""{ "tipo": "outra_loja", "busca": null }""")));
    }
}
