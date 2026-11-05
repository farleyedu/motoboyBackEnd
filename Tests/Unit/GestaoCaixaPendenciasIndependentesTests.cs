using System.Collections.Generic;
using APIBack.DTOs.Delivery;
using APIBack.GestaoLogica.Caixa;
using APIBack.GestaoLogica.Vendas;
using APIBack.Model.Delivery;
using APIBack.Model.Enum;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Entrega e pagamento são independentes: modalidade afirmada, recebimento antes de entregar e dinheiro que não é do motoboy.</summary>
public class GestaoCaixaPendenciasIndependentesTests
{
    [Theory]
    [InlineData("delivery", "delivery")]
    [InlineData(" Entrega ", "delivery")]
    [InlineData("retirada", "retirada")]
    [InlineData("balcão", "retirada")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void ModalidadeNormaliza(string? entrada, string? esperado) => Assert.Equal(esperado, PedidoModalidade.Normalize(entrada));

    [Fact]
    public void ModalidadeDesconhecidaEErroNuncaDescartada() =>
        Assert.Equal("INVALID_MODALIDADE", Assert.Throws<DeliveryDomainException>(() => PedidoModalidade.Normalize("drone")).Code);

    [Theory]
    [InlineData("DELIVERY", "delivery")]
    [InlineData("TAKEOUT", "retirada")]
    [InlineData("INDOOR", null)]
    [InlineData(null, null)]
    public void TipoDoIfood(string? tipo, string? esperado) => Assert.Equal(esperado, PedidoModalidade.DoTipoIfood(tipo));

    private static AntecipadoRegras.Barra? Barreira(int status = (int)StatusPedido.Pendente, string? pago = "Pendente", string? tipo = null,
        bool motoboy = false, bool ifood = false) => AntecipadoRegras.Barreira(status, pago, tipo, "Dinheiro", motoboy, ifood);

    [Fact]
    public void PedidoEmOperacaoPodeSerRecebidoAntesDeEntregar() => Assert.Null(Barreira());

    [Fact]
    public void JaPagoNaoCobraDeNovo()
    {
        var mesma = Barreira(pago: "Pago", tipo: "Dinheiro");
        Assert.True(mesma!.Replay);
        var outra = Barreira(pago: "Pago", tipo: "Pix");
        Assert.False(outra!.Replay);
        Assert.Equal("JA_PAGO", outra.Codigo);
    }

    [Fact]
    public void ComMotoboyNaoSeCobraNoCaixa() => Assert.Equal("COM_MOTOBOY", Barreira(motoboy: true)!.Codigo);

    [Fact]
    public void IfoodNaoSeRecebeNoCaixa() => Assert.Equal("IFOOD", Barreira(ifood: true)!.Codigo);

    [Theory]
    [InlineData((int)StatusPedido.Concluido)]
    [InlineData((int)StatusPedido.Cancelado)]
    [InlineData((int)StatusPedido.EmRota)]
    [InlineData((int)StatusPedido.Rascunho)]
    public void SoPedidoAindaNaLoja(int status) => Assert.Equal("PEDIDO_NAO_ABERTO", Barreira(status)!.Codigo);

    [Fact]
    public void AdiantamentoViraVendaSemNovaEntrada()
    {
        var p = Assert.Single(VendaRegras.ComAdiantamento(50m, "din", 50m));
        Assert.True(p.Adiantada);
        Assert.True(p.Recebida);
        Assert.Equal(50m, p.Valor);
    }

    [Fact]
    public void PedidoQueCresceuDepoisDoAdiantamentoDeixaADiferencaAReceber()
    {
        var partes = VendaRegras.ComAdiantamento(60m, "pix", 50m);
        Assert.Equal(2, partes.Count);
        Assert.Equal(50m, partes[0].Valor);
        Assert.True(partes[0].Adiantada);
        Assert.Equal(10m, partes[1].Valor);
        Assert.False(partes[1].Recebida);
        Assert.False(partes[1].Adiantada);
    }

    [Fact]
    public void PedidoQueDiminuiuNaoFaturaMaisQueOTotal() =>
        Assert.Equal(40m, Assert.Single(VendaRegras.ComAdiantamento(40m, "din", 50m)).Valor);

    [Fact]
    public void MaquininhaContaOAdiantamentoUmaVezSo()
    {
        // Recebeu 50 em Pix antes, concluiu no mesmo turno: a maquininha só viu 50.
        var fita = new[]
        {
            new LinhaFita("a", "10:00", "adiant", "Pago antes", "pix", 50m, Destino: FormaDestino.Maquininha),
            new LinhaFita("v", "11:00", "venda", "Pedido", "pix", 50m, Destino: FormaDestino.Adiantamento),
            new LinhaFita("o", "11:30", "venda", "Outro", "pix", 30m, Destino: FormaDestino.Maquininha),
        };
        Assert.Equal(80m, CaixaRegras.MaquininhaEsperada(fita, "pix"));
    }

    [Fact]
    public void AdiantamentoDevolvidoSaiDaMaquininha()
    {
        var fita = new[]
        {
            new LinhaFita("a", "10:00", "adiant", "Pago antes", "deb", 50m, Destino: FormaDestino.Maquininha),
            new LinhaFita("c", "10:30", "canc", "Cancelado", "deb", -50m, Destino: FormaDestino.Maquininha),
        };
        Assert.Equal(0m, CaixaRegras.MaquininhaEsperada(fita, "deb"));
    }

    [Fact]
    public void PagoAntesNaoFicaComOMotoboy() => Assert.True(VendaRegras.PagoAntesDaEntrega("Pago", null, ifood: false));

    [Fact]
    public void CobradoNaEntregaFicaComOMotoboy()
    {
        var conferidos = new List<DeliveryPaymentPart> { new() { Method = "dinheiro", Amount = 10, ReceivedConfirmed = true } };
        Assert.False(VendaRegras.PagoAntesDaEntrega("pago", conferidos, ifood: false));
    }

    [Fact]
    public void SemPagamentoNaoEPagoAntes() => Assert.False(VendaRegras.PagoAntesDaEntrega("Pendente", null, ifood: false));

    [Fact]
    public void IfoodTemRegraPropria() => Assert.False(VendaRegras.PagoAntesDaEntrega("Pago", null, ifood: true));
}
