using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Estoque;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Consumo da baixa por venda (ficha × modalidade) e divergência de inventário.</summary>
public class GestaoEstoqueRegrasTests
{
    private static readonly Guid Burger = Guid.NewGuid(), Pao = Guid.NewGuid(), Queijo = Guid.NewGuid(), Caixa = Guid.NewGuid();

    private static readonly Dictionary<Guid, FichaTecnica> Fichas = new()
    {
        [Burger] = new FichaTecnica(Burger, new[]
        {
            new ComponenteFicha(Pao, "Pão brioche", 1, "un"),
            new ComponenteFicha(Queijo, "Muçarela", 30, "g"),
            new ComponenteFicha(Caixa, "Caixa kraft", 1, "un", new[] { Modalidade.Delivery }),
        }),
    };

    [Fact]
    public void Venda_baixa_insumos_e_embalagem_so_no_delivery()
    {
        var (consumo, pedidos, semFicha) = EstoqueRegras.Consumo(
            new[] { new PedidoBaixa(1, Modalidade.Delivery), new PedidoBaixa(2, Modalidade.Salao) },
            new[] { new ItemVendido(1, Burger, "X-Burger", 2), new ItemVendido(2, Burger, "X-Burger", 1) }, Fichas);
        Assert.Equal(new[] { 1, 2 }, pedidos);
        Assert.Empty(semFicha);
        Assert.Equal(3m, consumo.Single(c => c.InsumoId == Pao).Quantidade);
        Assert.Equal(90m, consumo.Single(c => c.InsumoId == Queijo).Quantidade);
        Assert.Equal(2m, consumo.Single(c => c.InsumoId == Caixa).Quantidade); // o salão não leva caixa
    }

    [Fact]
    public void Pedido_com_item_sem_ficha_fica_de_fora_inteiro()
    {
        var (consumo, pedidos, semFicha) = EstoqueRegras.Consumo(new[] { new PedidoBaixa(3, Modalidade.Retirada) },
            new[] { new ItemVendido(3, Burger, "X-Burger", 1), new ItemVendido(3, null, "Refri avulso", 1) }, Fichas);
        Assert.Empty(consumo);
        Assert.Empty(pedidos);
        Assert.Equal("Refri avulso", Assert.Single(Assert.Single(semFicha).Itens));
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() =>
            EstoqueRegras.ValidarPedidos(new[] { new PedidoBaixa(1, "balcao") })).Codigo);
    }

    [Fact]
    public void Inventario_compara_contado_com_saldo_e_recusa_contagem_invalida()
    {
        var saldos = new Dictionary<Guid, SaldoInsumo>
        {
            [Pao] = new(Pao, "Pão brioche", "un", 13), [Queijo] = new(Queijo, "Muçarela", "g", -90),
        };
        var d = EstoqueRegras.Divergencias(new[] { new Contagem(Pao, 12), new Contagem(Queijo, 0) }, saldos);
        Assert.Equal((-1m, 90m), (d[0].Diferenca, d[1].Diferenca));
        Assert.Throws<GestaoException>(() => EstoqueRegras.Divergencias(new[] { new Contagem(Pao, -1) }, saldos));
        Assert.Throws<GestaoException>(() => EstoqueRegras.Divergencias(new[] { new Contagem(Pao, 1), new Contagem(Pao, 2) }, saldos));
        Assert.Throws<GestaoException>(() => EstoqueRegras.Divergencias(new[] { new Contagem(Guid.NewGuid(), 1) }, saldos));
    }
}
