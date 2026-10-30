using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vendas;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class FichaRegrasTests
{
    private static readonly Guid Pao = Guid.NewGuid(), Caixa = Guid.NewGuid(), Velho = Guid.NewGuid();

    private static readonly Dictionary<Guid, InsumoFicha> Insumos = new()
    {
        [Pao] = new("Pão brioche", "un", "Padaria", true),
        [Caixa] = new("Caixa kraft M", "un", "Embalagens", true),
        [Velho] = new("Molho antigo", "ml", "Mercearia", false),
    };

    [Fact]
    public void Embalagem_delivery_fica_so_no_delivery_e_ingrediente_vale_para_todas()
    {
        var itens = FichaRegras.Validar(new[]
        {
            new ComponenteRequest(Pao, 1, "un", null),
            new ComponenteRequest(Caixa, 1, "un", new[] { "delivery" }),
        }, Insumos);
        Assert.Null(itens[0].Modalidades);
        Assert.Equal(new[] { "delivery" }, itens[1].Modalidades);
        Assert.True(FichaRegras.Embalagem("Embalagens"));
        Assert.True(FichaRegras.Embalagem("Descartáveis"));
        Assert.False(FichaRegras.Embalagem("Padaria"));
    }

    [Fact]
    public void As_tres_modalidades_juntas_viram_todas_e_a_ordem_e_normalizada()
    {
        Assert.Null(FichaRegras.Modalidades(new[] { "delivery", "salao", "retirada" }));
        Assert.Null(FichaRegras.Modalidades(Array.Empty<string>()));
        Assert.Equal(new[] { "salao", "delivery" }, FichaRegras.Modalidades(new[] { "Delivery", "salao", "delivery" }));
    }

    [Fact]
    public void Modalidade_desconhecida_e_erro_nunca_descartada()
    {
        var ex = Assert.Throws<GestaoException>(() => FichaRegras.Modalidades(new[] { "mesa" }));
        Assert.Equal("CAMPO_INVALIDO", ex.Codigo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantidade_precisa_ser_maior_que_zero(double quantidade)
    {
        Assert.Throws<GestaoException>(() => FichaRegras.Validar(new[] { new ComponenteRequest(Pao, (decimal)quantidade, "un", null) }, Insumos));
    }

    [Fact]
    public void Insumo_de_outra_loja_inativo_ou_em_outra_unidade_e_barrado()
    {
        Assert.Throws<GestaoException>(() => FichaRegras.Validar(new[] { new ComponenteRequest(Guid.NewGuid(), 1, "un", null) }, Insumos));
        Assert.Throws<GestaoException>(() => FichaRegras.Validar(new[] { new ComponenteRequest(Velho, 10, "ml", null) }, Insumos));
        Assert.Throws<GestaoException>(() => FichaRegras.Validar(new[] { new ComponenteRequest(Pao, 1, "g", null) }, Insumos));
    }

    [Fact]
    public void Mesmo_insumo_nao_pode_valer_duas_vezes_na_mesma_modalidade()
    {
        // "todas" + "delivery" se sobrepõem: a baixa descontaria o pão duas vezes no delivery.
        Assert.Throws<GestaoException>(() => FichaRegras.Validar(new[]
        {
            new ComponenteRequest(Caixa, 1, "un", null), new ComponenteRequest(Caixa, 1, "un", new[] { "delivery" }),
        }, Insumos));
        // Grupos que não se cruzam são válidos.
        var ok = FichaRegras.Validar(new[]
        {
            new ComponenteRequest(Caixa, 1, "un", new[] { "delivery" }), new ComponenteRequest(Caixa, 2, "un", new[] { "salao", "retirada" }),
        }, Insumos);
        Assert.Equal(2, ok.Count);
    }

    [Fact]
    public void Ficha_vazia_e_valida_e_limpa_o_produto()
    {
        Assert.Empty(FichaRegras.Validar(null, Insumos));
        Assert.Empty(FichaRegras.Validar(Array.Empty<ComponenteRequest>(), Insumos));
    }
}
