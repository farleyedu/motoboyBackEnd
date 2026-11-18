using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using APIBack.Assistente;
using Xunit;

namespace APIBack.Tests.Unit.Assistente;

public sealed class CatalogoAssistenteTests
{
    private const string ItemValido = """
        { "id": "cardapio.pausar-produto", "nome": "Pausar", "area": "Cardápio", "oQueFaz": "Some por um tempo.",
          "comoAsPessoasPedem": ["pausar"], "modo": "ia_faz", "acao": "produto.pausar", "rota": "/apps/cardapio",
          "passos": ["Abra Cardápio"], "permissao": "Cardapio:editar", "desfazer": null }
        """;

    private static CatalogoAssistente Carregar(params (string Nome, string Conteudo)[] arquivos) => CatalogoAssistente.Carregar(arquivos);

    private static string Item(Action<Dictionary<string, object?>> mudar)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(ItemValido)!;
        mudar(d);
        return JsonSerializer.Serialize(d);
    }

    [Fact]
    public void CatalogoEmbutidoCarregaSemErro()
    {
        // Lê todos os Assistente/Catalogo/*.json embutidos (o exemplo e os arquivos de área). Falha aqui = falha no boot.
        var catalogo = CatalogoAssistente.CarregarEmbutido();
        Assert.NotEmpty(catalogo.Itens);
        Assert.Equal(catalogo.Itens.Count, catalogo.Itens.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void ExemploTemUmDeCadaModoEOParPausarVoltar()
    {
        var conteudo = File.ReadAllText(Path.Combine(RaizProjeto(), "Assistente", "Catalogo", "_exemplo.json"));
        var catalogo = Carregar(("_exemplo.json", conteudo));
        Assert.Equal(5, catalogo.Itens.Count);
        Assert.All(ModosCatalogo.Todos, m => Assert.Contains(catalogo.Itens, i => i.Modo == m));
        var pausar = catalogo.Obter("exemplo.pausar-produto")!;
        var voltar = catalogo.Obter(pausar.Desfazer)!;
        Assert.Equal("produto.voltar", voltar.Acao);
        Assert.Equal(pausar.Id, voltar.Desfazer);
    }

    [Fact]
    public void ArquivoDeReferenciaSoServeSemArquivoDeArea()
    {
        var area = Item(d => d["id"] = "loja.outra");
        var catalogo = Carregar(("_exemplo.json", $"[{ItemValido}]"), ("loja.json", $"[{area}]"));
        Assert.Equal(["loja.outra"], catalogo.Itens.Select(i => i.Id));
    }

    [Fact]
    public void IdRepetidoEntreArquivosFalha()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{ItemValido}]"), ("b.json", $"[{ItemValido}]")));
        Assert.Contains("id repetido", ex.Message);
    }

    [Theory]
    [InlineData("id", "Cardapio.Pausar", "id deve ser")]
    [InlineData("modo", "faz", "modo deve ser")]
    [InlineData("rota", "apps/cardapio", "rota deve começar")]
    [InlineData("acao", null, "acao é obrigatória")]
    [InlineData("permissao", "cardapio", "permissao deve ser")]
    [InlineData("nome", "", "nome é obrigatório")]
    [InlineData("oQueFaz", " ", "oQueFaz é obrigatório")]
    public void ItemInvalidoFalhaComMotivo(string campo, string? valor, string motivo)
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{Item(d => d[campo] = valor)}]")));
        Assert.Contains(motivo, ex.Message);
    }

    [Fact]
    public void AcaoForaDoModoIaFazFalha()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{Item(d => d["modo"] = "tela")}]")));
        Assert.Contains("acao só vale no modo ia_faz", ex.Message);
    }

    [Fact]
    public void CampoDesconhecidoFalha()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{Item(d => d["rotas"] = "/x")}]")));
        Assert.Contains("JSON inválido", ex.Message);
    }

    [Fact]
    public void DesfazerParaItemInexistenteFalha()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{Item(d => d["desfazer"] = "cardapio.nao-existe")}]")));
        Assert.Contains("desfazer aponta", ex.Message);
    }

    [Fact]
    public void FrasesVaziasFalham()
    {
        var ex = Assert.Throws<CatalogoInvalidoException>(() => Carregar(("a.json", $"[{Item(d => d["comoAsPessoasPedem"] = Array.Empty<string>())}]")));
        Assert.Contains("comoAsPessoasPedem", ex.Message);
    }

    [Fact]
    public void PromptMandaIaFazSemExecutorComoTela()
    {
        var catalogo = Carregar(("a.json", $"[{ItemValido}]"));
        Assert.Contains("\"modo\":\"ia_faz\"", catalogo.ParaPrompt(_ => true));
        var semExecutor = catalogo.ParaPrompt(_ => false);
        Assert.Contains("\"modo\":\"tela\"", semExecutor);
        Assert.DoesNotContain("produto.pausar", semExecutor);
    }

    [Fact]
    public void PromptEhEstavel()
    {
        var a = Item(d => d["id"] = "loja.b");
        var b = Item(d => d["id"] = "loja.a");
        Assert.Equal(Carregar(("x.json", $"[{a},{b}]")).ParaPrompt(_ => true), Carregar(("x.json", $"[{b},{a}]")).ParaPrompt(_ => true));
    }

    internal static string RaizProjeto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "APIBack.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("APIBack.csproj não encontrado");
    }
}
