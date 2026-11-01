using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>6C5: de onde vem cada parte do acerto do motoboy, a diferença com motivo e a situação do lote.</summary>
public class GestaoCaixaAcertoRegrasTests
{
    [Theory]
    [InlineData("recebivel", "motoboys", "a_receber", ParteAcerto.Dinheiro)]
    [InlineData("recebivel", "motoboys", "recebido", ParteAcerto.Outro)]
    [InlineData("recebivel", "maquininha", "recebido", ParteAcerto.Integracao)]
    [InlineData("recebivel", "maquininha", "a_receber", ParteAcerto.CartaoManual)]
    [InlineData("recebivel", "ifood", "a_receber", ParteAcerto.Ifood)]
    [InlineData("caixa", null, "recebido", ParteAcerto.Outro)]
    [InlineData(null, null, "a_receber", ParteAcerto.Outro)]
    public void Gestao6C5_classifica_a_parte_pela_conta_do_livro(string? tipo, string? subtipo, string status, string esperado) =>
        Assert.Equal(esperado, AcertoRegras.Classificar(tipo, subtipo, status));

    [Fact]
    public void Gestao6C5_diferenca_exige_motivo_e_valor_valido()
    {
        Assert.Equal(0m, AcertoRegras.Diferenca(66.90m, 66.90m, null));
        Assert.Equal(-6.90m, AcertoRegras.Diferenca(66.90m, 60m, "faltou troco"));
        Assert.Equal(3.10m, AcertoRegras.Diferenca(66.90m, 70m, "sobrou"));
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => AcertoRegras.Diferenca(66.90m, 60m, " ")).Codigo);
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => AcertoRegras.Diferenca(10m, -1m, "x y z")).Codigo);
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => AcertoRegras.Diferenca(10m, 10.001m, null)).Codigo);
    }

    [Fact]
    public void Gestao6C5_lote_parcial_quando_sobra_pedido_com_o_motoboy()
    {
        Assert.Equal("conferido", AcertoRegras.Situacao(0));
        Assert.Equal("parcial", AcertoRegras.Situacao(2));
        Assert.Equal("L-0007", AcertoRegras.Numero(7));
    }
}
