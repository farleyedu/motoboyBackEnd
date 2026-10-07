using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DeliveryArrivalRulesTests
    {
        [Fact]
        public void RecusaChegadaDaTelaAnteriorDepoisDeTransferencia()
        {
            var error = Assert.Throws<DeliveryDomainException>(() => DeliveryArrivalRules.EnsureExpectedPedido(1842, 1843));
            Assert.Equal("CURRENT_DELIVERY_CHANGED", error.Code);
            Assert.Equal(409, error.StatusCode);
        }
        [Theory]
        [InlineData(1842, 1842)]
        [InlineData(null, 1842)]
        public void AceitaPedidoAtualEClienteLegado(int? expected, int current) =>
            DeliveryArrivalRules.EnsureExpectedPedido(expected, current);
    }
}
