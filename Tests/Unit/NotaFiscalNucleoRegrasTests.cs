using System;
using System.Linq;
using APIBack.Integrations.NotaFiscal.Nucleo;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class NotaFiscalNucleoRegrasTests
{
    [Theory]
    [InlineData("22021000", "22021000")]
    [InlineData("2202.10.00", "22021000")]
    [InlineData(" 2202 1000 ", "22021000")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NcmAceitaPontuacaoEVazio(string? entrada, string? esperado) =>
        Assert.Equal(esperado, NotaFiscalRegras.NormalizarNcm(entrada));

    [Theory]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("abcdefgh")]
    public void NcmForaDeOitoDigitosEhRecusado(string entrada)
    {
        var ex = Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.NormalizarNcm(entrada));
        Assert.Contains("8 digitos", ex.Message);
    }

    [Theory]
    [InlineData("5102", "5102")]
    [InlineData("5.405", "5405")]
    [InlineData("6102", "6102")]
    public void CfopDeSaidaEhAceito(string entrada, string esperado) => Assert.Equal(esperado, NotaFiscalRegras.NormalizarCfop(entrada));

    [Theory]
    [InlineData("1102")]   // entrada, nao venda
    [InlineData("510")]
    [InlineData("51022")]
    public void CfopInvalidoEhRecusado(string entrada) =>
        Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.NormalizarCfop(entrada));

    [Theory]
    [InlineData("102")]
    [InlineData("500")]
    [InlineData("900")]
    public void CsosnValidoPassa(string csosn) => Assert.Equal(csosn, NotaFiscalRegras.NormalizarCsosn(csosn));

    [Theory]
    [InlineData("999")]
    [InlineData("10")]
    [InlineData("abc")]
    public void CsosnInvalidoEhRecusado(string csosn) =>
        Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.NormalizarCsosn(csosn));

    [Theory]
    [InlineData("0", "00")]
    [InlineData("60", "60")]
    [InlineData("40", "40")]
    public void CstCompletaZeroAEsquerda(string entrada, string esperado) => Assert.Equal(esperado, NotaFiscalRegras.NormalizarCst(entrada));

    [Fact]
    public void CstInvalidoEhRecusado() => Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.NormalizarCst("99"));

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void OrigemDentroDoIntervalo(int origem) => Assert.Equal(origem, NotaFiscalRegras.ValidarOrigem(origem));

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void OrigemForaDoIntervaloEhRecusada(int origem) =>
        Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.ValidarOrigem(origem));

    [Fact]
    public void UnidadeFicaMaiuscula() => Assert.Equal("UN", NotaFiscalRegras.NormalizarUnidade(" un "));

    [Fact]
    public void UnidadeComNumeroEhRecusada() => Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.NormalizarUnidade("U1"));

    [Theory]
    [InlineData("22021000", "5102", "102", null, true)]
    [InlineData("22021000", "5102", null, "60", true)]
    [InlineData(null, "5102", "102", null, false)]      // sem NCM
    [InlineData("22021000", null, "102", null, false)]  // sem CFOP
    [InlineData("22021000", "5102", null, null, false)] // sem CSOSN nem CST
    [InlineData("  ", "5102", "102", null, false)]
    public void ProdutoCompletoExigeNcmCfopECsosnOuCst(string? ncm, string? cfop, string? csosn, string? cst, bool esperado) =>
        Assert.Equal(esperado, NotaFiscalRegras.ProdutoCompleto(ncm, cfop, csosn, cst));

    [Fact]
    public void ChaveSemEspacoPassaEVaziaViraNula()
    {
        Assert.Equal("abcdefgh1234", NotaFiscalRegras.ValidarChave("  abcdefgh1234  "));
        Assert.Null(NotaFiscalRegras.ValidarChave("   "));
        Assert.Null(NotaFiscalRegras.ValidarChave(null));
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("com espaco no meio da chave")]
    public void ChaveIncompletaEhRecusada(string chave) =>
        Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.ValidarChave(chave));

    [Fact]
    public void InscricaoEstadualAceitaIsentoENumeros()
    {
        Assert.Equal("ISENTO", NotaFiscalRegras.NormalizarInscricaoEstadual("isento"));
        Assert.Equal("123456789", NotaFiscalRegras.NormalizarInscricaoEstadual("123.456.789"));
        Assert.Null(NotaFiscalRegras.NormalizarInscricaoEstadual(" "));
    }

    [Fact]
    public void ChecklistComecaTodoPendente()
    {
        var itens = NotaFiscalRegras.Checklist(null, false, false, false, StatusConexao.NaoConectado, 3);
        Assert.Equal(new[] { "regime", "certificado", "csc", "chave", "teste", "produtos" }, itens.Select(i => i.Id));
        Assert.All(itens, i => Assert.False(i.Ok));
        Assert.Contains("3 produtos estao", itens.Last().Dica);
    }

    [Fact]
    public void ChecklistFicaVerdeQuandoTudoEstaPronto()
    {
        var itens = NotaFiscalRegras.Checklist(Regimes.Simples, true, true, true, StatusConexao.Conectado, 0);
        Assert.All(itens, i => Assert.True(i.Ok));
    }

    [Fact]
    public void ChecklistDizQueMeiPodeNaoPrecisar()
    {
        var regime = NotaFiscalRegras.Checklist(Regimes.Mei, false, false, false, StatusConexao.NaoConectado, 0).First(i => i.Id == "regime");
        Assert.True(regime.Ok);
        Assert.Contains("MEI", regime.Dica);
    }

    [Fact]
    public void ChecklistNoSingularDizUmProdutoEsta()
    {
        var produtos = NotaFiscalRegras.Checklist(null, false, false, false, StatusConexao.NaoConectado, 1).Last();
        Assert.Contains("1 produto esta", produtos.Dica);
    }

    [Fact]
    public void RegrasPadraoSaoManuaisComTodosOsCanaisLigados()
    {
        var r = NotaFiscalRegras.RegrasPadrao();
        Assert.Equal(ModosEmissao.Manual, r.Modo);
        Assert.Equal(5, r.EsperaMinutos);
        Assert.True(r.Canais.Cardapio && r.Canais.Balcao && r.Canais.Mesa && r.Canais.Ifood);
        Assert.True(r.FormasPagamento.Dinheiro && r.FormasPagamento.Pix && r.FormasPagamento.Debito && r.FormasPagamento.Credito && r.FormasPagamento.Online);
    }

    [Theory]
    [InlineData("outro", 5)]
    [InlineData("manual", -1)]
    [InlineData("automatico", 61)]
    public void RegrasInvalidasSaoRecusadas(string modo, int espera) =>
        Assert.Throws<NotaFiscalException>(() => NotaFiscalRegras.ValidarRegras(new RegrasDto { Modo = modo, EsperaMinutos = espera }));

    [Fact]
    public void ConexaoFiscalNaoExpoeAChaveNoToString()
    {
        var c = new ConexaoFiscal(Guid.NewGuid(), Provedores.Focus, Ambientes.Homologacao, "SEGREDO-SUPER-SECRETO", "123", null, null);
        Assert.DoesNotContain("SEGREDO", c.ToString());
    }

    [Fact]
    public void ResultadoEmpresaNaoExpoeTokensNoToString()
    {
        var r = new ResultadoEmpresa(true, "1", "TOKEN-HOMOLOG", "TOKEN-PROD", null, null);
        Assert.DoesNotContain("TOKEN", r.ToString());
    }

    [Fact]
    public void CatalogoTemFocusComCincoPassos()
    {
        var focus = Assert.Single(NotaFiscalRegras.CatalogoProvedores());
        Assert.Equal(Provedores.Focus, focus.Id);
        Assert.Equal(5, focus.Passos.Count);
    }
}
