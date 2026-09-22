using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class ManualOrderRulesTests
    {
        private static CreatePedidoRequest Valid() => new()
        {
            NomeCliente = "Maria",
            TelefoneCliente = "(34) 99999-0000",
            Rua = "Rua Duque de Caxias",
            Numero = "850",
            Bairro = "Lidice",
            Cidade = "Uberlandia",
            Estado = "mg",
            Cep = "38400144",
            Latitude = -18.922911,
            Longitude = -48.273639,
            Items = "Pizza",
            Value = 45.5m,
            TipoPagamento = "Dinheiro",
            Troco = 4.5m
        };

        [Fact]
        public void Validate_NormalizesAddressCepAndState()
        {
            var order = ManualOrderRules.Validate(Valid());

            Assert.Equal("Rua Duque de Caxias, 850 – Lidice", order.EnderecoEntrega);
            Assert.Equal("38400-144", order.Cep);
            Assert.Equal("MG", order.Estado);
            Assert.Equal(ManualOrderRules.DefaultPrevisaoMinutos, order.PrevisaoMinutos);
        }

        [Fact]
        public void Validate_IncludesComplementInAddress()
        {
            var request = Valid();
            request.Complemento = "Apto 12";

            Assert.Equal("Rua Duque de Caxias, 850 (Apto 12) – Lidice", ManualOrderRules.Validate(request).EnderecoEntrega);
        }

        [Fact]
        public void Validate_RequiresCoordinates_NeverInventsPosition()
        {
            var request = Valid();
            request.Latitude = null;

            var exception = Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(request));
            Assert.Equal("INVALID_ORDER", exception.Code);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(91, 0)]
        [InlineData(0, 181)]
        public void Validate_RejectsInvalidCoordinates(double latitude, double longitude)
        {
            var request = Valid();
            request.Latitude = latitude;
            request.Longitude = longitude;

            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(request));
        }

        [Theory]
        [InlineData(nameof(CreatePedidoRequest.NomeCliente))]
        [InlineData(nameof(CreatePedidoRequest.Rua))]
        [InlineData(nameof(CreatePedidoRequest.Numero))]
        [InlineData(nameof(CreatePedidoRequest.Bairro))]
        [InlineData(nameof(CreatePedidoRequest.Cidade))]
        public void Validate_RequiresAddressAndCustomer(string field)
        {
            var request = Valid();
            typeof(CreatePedidoRequest).GetProperty(field)!.SetValue(request, "  ");

            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(request));
        }

        [Fact]
        public void Validate_RejectsBadCepStateAndPrevision()
        {
            var badCep = Valid();
            badCep.Cep = "1234";
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(badCep));

            var badState = Valid();
            badState.Estado = "M1";
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(badState));

            var badPrevision = Valid();
            badPrevision.PrevisaoMinutos = 0;
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(badPrevision));
        }

        [Fact]
        public void Validate_RejectsNegativeValues()
        {
            var negativeValue = Valid();
            negativeValue.Value = -1;
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(negativeValue));

            var negativeChange = Valid();
            negativeChange.Troco = -1;
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(negativeChange));
        }

        [Fact]
        public void Validate_NullRequest_Throws()
        {
            Assert.Throws<DeliveryDomainException>(() => ManualOrderRules.Validate(null));
        }
    }
}
