using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Caixa;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Contagem por cédula, diferença, troco que fica, movimentos de gaveta e justificativa.</summary>
public class GestaoCaixaRegrasTests
{
    [Fact]
    public void Conta_cedulas_e_moedas_e_recusa_contagem_invalida()
    {
        Assert.Equal(283.55m, CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [100] = 2, [50] = 1, [20] = 1, [10] = 1, [2] = 1 }, 1.55m)));
        Assert.Throws<GestaoException>(() => CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [3] = 1 }, 0)));
        Assert.Throws<GestaoException>(() => CaixaRegras.TotalContado(new ContagemCaixa(new Dictionary<int, int> { [10] = -1 }, 0)));
    }

    [Fact]
    public void Diferenca_justificativa_e_troco_que_fica()
    {
        Assert.Equal(-2m, CaixaRegras.Diferenca(25, 23));
        Assert.Throws<GestaoException>(() => CaixaRegras.ExigirJustificativa(-2, " "));
        CaixaRegras.ExigirJustificativa(0, null);
        Assert.Equal((10m, 13m), CaixaRegras.Repartir(23, 10));
        Assert.Equal((23m, 0m), CaixaRegras.Repartir(23, 50)); // não fica mais do que há
        Assert.True(CaixaRegras.MaquininhaConfere(96.5m, 96.505m));
    }

    [Fact]
    public void Movimento_nao_tira_mais_do_que_ha_na_gaveta()
    {
        Assert.Equal("SEM_SALDO", Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento(MovimentoCaixa.Sangria, 200, 192.9m)).Codigo);
        CaixaRegras.ValidarMovimento(MovimentoCaixa.Suprimento, 200, 0);
        Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento(MovimentoCaixa.Pagamento, 0, 10));
        Assert.Throws<GestaoException>(() => CaixaRegras.ValidarMovimento("troco", 1, 10));
    }
}
