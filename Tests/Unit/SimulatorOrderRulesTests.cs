using System.Linq;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class SimulatorOrderRulesTests
    {
        private static object? Value(SimulatorOrderPatch patch, string column) =>
            patch.Columns.Single(item => item.Column == column).Value;

        [Fact]
        public void Null_KeepsTheField_EmptyTextClearsIt()
        {
            var patch = SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Observacoes = "", NomeCliente = "  Ana  " });

            Assert.Null(Value(patch, "observacoes"));
            Assert.Equal("Ana", Value(patch, "nome_cliente"));
            Assert.DoesNotContain(patch.Columns, item => item.Column == "telefone_cliente");
        }

        [Fact]
        public void Neighborhood_AlsoUpdatesTheRegionColumn()
        {
            var patch = SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Bairro = "Santa Monica" });

            Assert.Equal("Santa Monica", Value(patch, "entrega_bairro"));
            Assert.Equal("Santa Monica", Value(patch, "region"));
            Assert.True(patch.TouchesAddress);
        }

        [Fact]
        public void Coordinates_MustComeTogetherAndBeValid()
        {
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Latitude = -18.9 }));
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Latitude = 0, Longitude = 0 }));
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Latitude = 95, Longitude = -48 }));

            var patch = SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Latitude = -18.9186, Longitude = -48.2772 });
            Assert.Equal(-18.9186, Value(patch, "latitude"));
            Assert.Equal(-48.2772, Value(patch, "longitude"));
        }

        [Fact]
        public void Deadline_AllowsNegativeMinutesToSimulateLateOrders()
        {
            Assert.Equal(-4320, SimulatorOrderRules.Validate(new SimulatorPedidoRequest { PrevisaoEmMinutos = -4320 }).PrevisaoEmMinutos);
            Assert.Throws<DeliveryDomainException>(() =>
                SimulatorOrderRules.Validate(new SimulatorPedidoRequest { PrevisaoEmMinutos = SimulatorOrderRules.MaxFutureMinutes + 1 }));
        }

        [Fact]
        public void PlacedMinutesAgo_CannotBeNegative()
        {
            Assert.Equal(90, SimulatorOrderRules.Validate(new SimulatorPedidoRequest { PedidoHaMinutos = 90 }).PedidoHaMinutos);
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { PedidoHaMinutos = -5 }));
        }

        [Fact]
        public void ChangeAmount_ZeroClearsIt_AndValueIsRounded()
        {
            var patch = SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Troco = 0, Value = 42.906m });

            Assert.Null(Value(patch, "troco"));
            Assert.Equal(42.91m, Value(patch, "value"));
        }

        [Fact]
        public void PostalCodeAndState_AreNormalized()
        {
            var patch = SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Cep = "38400000", Estado = "mg" });

            Assert.Equal("38400-000", Value(patch, "entrega_cep"));
            Assert.Equal("MG", Value(patch, "entrega_estado"));
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Cep = "123" }));
            Assert.Throws<DeliveryDomainException>(() => SimulatorOrderRules.Validate(new SimulatorPedidoRequest { Estado = "M1" }));
        }

        [Fact]
        public void EmptyRequest_IsEmpty()
        {
            Assert.True(SimulatorOrderRules.Validate(new SimulatorPedidoRequest()).IsEmpty);
        }

        [Theory]
        [InlineData(APIBack.Model.Enum.StatusPedido.Concluido, true)]
        [InlineData(APIBack.Model.Enum.StatusPedido.Cancelado, true)]
        // Atribuido/em rota exigem motoboy + parada de rota; pendente/rascunho ja estao "abertos".
        [InlineData(APIBack.Model.Enum.StatusPedido.Atribuido, false)]
        [InlineData(APIBack.Model.Enum.StatusPedido.EmRota, false)]
        [InlineData(APIBack.Model.Enum.StatusPedido.Pendente, false)]
        [InlineData(APIBack.Model.Enum.StatusPedido.Rascunho, false)]
        public void Reopen_OnlyFinishedOrders_ComeBackAsPendingNeverAsInRoute(APIBack.Model.Enum.StatusPedido status, bool expected) =>
            Assert.Equal(expected, SimulatorOrderRules.CanReopen(status));

        [Fact]
        public void ComposeAddress_MatchesTheManualOrderFormat()
        {
            Assert.Equal("Av. Rondon Pacheco, 3200 – Santa Monica", SimulatorOrderRules.ComposeAddress("Av. Rondon Pacheco", "3200", null, "Santa Monica"));
            Assert.Equal("Rua A, 10 (ap 2) – Centro", SimulatorOrderRules.ComposeAddress("Rua A", "10", "ap 2", "Centro"));
        }
    }
}
