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
