using System;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DeliverySettingsRulesTests
    {
        // ---- Politica de transferencia --------------------------------------------------

        [Theory]
        [InlineData("direct", true)]
        [InlineData("establishment_approval", true)]
        [InlineData("disabled", true)]
        [InlineData("operator", false)]
        [InlineData("qualquer", false)]
        public void TransferPolicy_OnlyThreeValuesAreConfigurable(string value, bool expected)
        {
            Assert.Equal(expected, TransferPolicies.IsConfigurable(value));
        }

        [Fact]
        public void TransferPolicy_DisabledBlocksTheMotoboyOnly()
        {
            Assert.False(TransferPolicies.AllowsMotoboyTransfer(TransferPolicies.Disabled));
            Assert.True(TransferPolicies.AllowsMotoboyTransfer(TransferPolicies.Direct));
            Assert.True(TransferPolicies.AllowsMotoboyTransfer(TransferPolicies.EstablishmentApproval));
        }

        private static (PedidoQueueService Service, Mock<IPedidoQueueRepository> Repository) CreateService()
        {
            var repository = new Mock<IPedidoQueueRepository>();
            return (new PedidoQueueService(repository.Object), repository);
        }

        [Fact]
        public async Task UpdateSettings_AcceptsDisabledAndSavesIt()
        {
            var (service, repository) = CreateService();
            repository
                .Setup(item => item.UpsertSettingsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<UpdateDeliverySettingsRequest>()))
                .ReturnsAsync(new DeliverySettingsDto());

            await service.UpdateSettingsAsync(Guid.NewGuid(), 1, new UpdateDeliverySettingsRequest { TransferPolicy = "  DISABLED " });

            repository.Verify(item => item.UpsertSettingsAsync(
                It.IsAny<Guid>(), 1, It.Is<UpdateDeliverySettingsRequest>(request => request.TransferPolicy == "disabled")), Times.Once);
        }

        [Fact]
        public async Task UpdateSettings_RejectsUnknownPolicy()
        {
            var (service, repository) = CreateService();

            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.UpdateSettingsAsync(Guid.NewGuid(), 1, new UpdateDeliverySettingsRequest { TransferPolicy = "operator" }));

            Assert.Equal("INVALID_TRANSFER_POLICY", error.Code);
            repository.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(601)]
        public async Task UpdateSettings_RejectsDefaultDeadlineOutOfRange(int minutes)
        {
            var (service, _) = CreateService();

            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.UpdateSettingsAsync(Guid.NewGuid(), 1, new UpdateDeliverySettingsRequest { TransferPolicy = "direct", DefaultDeliveryMinutes = minutes }));

            Assert.Equal("INVALID_DEFAULT_DELIVERY_MINUTES", error.Code);
        }

        // ---- Prazo padrao ao criar pedido --------------------------------------------------

        [Fact]
        public async Task CreatePedido_UsesTheConfiguredDefaultDeadlineWhenNoneIsGiven()
        {
            var (service, repository) = CreateService();
            repository.Setup(item => item.GetSettingsAsync(It.IsAny<Guid>()))
                .ReturnsAsync(new DeliverySettingsDto { DefaultDeliveryMinutes = 55 });
            ManualOrder? saved = null;
            repository.Setup(item => item.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
                .Callback<Guid, int, ManualOrder>((_, _, order) => saved = order)
                .ReturnsAsync(new CreatedPedidoDto { Id = 1 });

            await service.CreatePedidoAsync(Guid.NewGuid(), 1, ValidOrder());

            Assert.Equal(55, saved!.PrevisaoMinutos);
        }

        [Fact]
        public async Task CreatePedido_KeepsTheDeadlineTheOperatorTyped()
        {
            var (service, repository) = CreateService();
            ManualOrder? saved = null;
            repository.Setup(item => item.CreatePedidoAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ManualOrder>()))
                .Callback<Guid, int, ManualOrder>((_, _, order) => saved = order)
                .ReturnsAsync(new CreatedPedidoDto { Id = 1 });
            var request = ValidOrder();
            request.PrevisaoMinutos = 25;

            await service.CreatePedidoAsync(Guid.NewGuid(), 1, request);

            Assert.Equal(25, saved!.PrevisaoMinutos);
            repository.Verify(item => item.GetSettingsAsync(It.IsAny<Guid>()), Times.Never);
        }

        private static CreatePedidoRequest ValidOrder() => new()
        {
            NomeCliente = "Ana", Rua = "Rua A", Numero = "10", Bairro = "Centro", Cidade = "Uberlandia",
            Latitude = -18.9186, Longitude = -48.2772,
        };

        // ---- Dados do restaurante ------------------------------------------------------------

        private static UpdateRestaurantSettingsRequest Restaurant() => new()
        {
            Logradouro = "  Av. Joao Naves de Avila ", Numero = "1200", Bairro = "Santa Monica", Cidade = "Uberlandia",
            Uf = "mg", Cep = "38408100", Latitude = -18.9186, Longitude = -48.2772, RaioEntregaKm = 7.456m, TempoPreparoMin = 25,
        };

        [Fact]
        public void Restaurant_NormalizesTextStateAndPostalCode()
        {
            var result = RestaurantSettingsRules.Validate(Restaurant());

            Assert.Equal("Av. Joao Naves de Avila", result.Logradouro);
            Assert.Equal("MG", result.Uf);
            Assert.Equal("38408-100", result.Cep);
            Assert.Equal(7.46m, result.RaioEntregaKm);
        }

        [Fact]
        public void Restaurant_EmptyTextClearsTheField()
        {
            var request = Restaurant();
            request.Complemento = "   ";

            Assert.Null(RestaurantSettingsRules.Validate(request).Complemento);
        }

        [Fact]
        public void Restaurant_CoordinatesMustComeTogetherAndBeValid()
        {
            var one = Restaurant(); one.Longitude = null;
            var zero = Restaurant(); zero.Latitude = 0; zero.Longitude = 0;
            var range = Restaurant(); range.Latitude = 95;
            var none = Restaurant(); none.Latitude = null; none.Longitude = null;

            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(one));
            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(zero));
            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(range));
            Assert.Null(RestaurantSettingsRules.Validate(none).Latitude);
        }

        [Theory]
        [InlineData("Uf", "M1")]
        [InlineData("Cep", "123")]
        public void Restaurant_RejectsBadStateAndPostalCode(string field, string value)
        {
            var request = Restaurant();
            if (field == "Uf") request.Uf = value; else request.Cep = value;

            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(request));
        }

        [Fact]
        public void Restaurant_RejectsNegativeOrAbsurdRulesButAllowsEmpty()
        {
            var negative = Restaurant(); negative.PedidoMinimo = -1;
            var radius = Restaurant(); radius.RaioEntregaKm = 501;
            var prep = Restaurant(); prep.TempoPreparoMin = 601;
            var empty = Restaurant(); empty.RaioEntregaKm = null; empty.TempoPreparoMin = null;

            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(negative));
            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(radius));
            Assert.Throws<DeliveryDomainException>(() => RestaurantSettingsRules.Validate(prep));
            Assert.Null(RestaurantSettingsRules.Validate(empty).RaioEntregaKm);
        }
    }
}
