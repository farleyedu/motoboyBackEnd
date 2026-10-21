using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Controllers;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class PedidoQueueOperatorActionsTests
    {
        private readonly Mock<IPedidoQueueRepository> _repository = new();
        private PedidoQueueService Service => new(_repository.Object);

        [Fact]
        public async Task TransferRoute_RejectsSameMotoboy()
        {
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Service.TransferRouteByOperatorAsync(Guid.NewGuid(), 1, fromMotoboyId: 7, toMotoboyId: 7, motivo: null));
            Assert.Equal("TRANSFER_SAME_MOTOBOY", error.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(0, 8)]
        [InlineData(7, 0)]
        public async Task TransferRoute_RejectsInvalidIds(int from, int to)
        {
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Service.TransferRouteByOperatorAsync(Guid.NewGuid(), 1, from, to, null));
            Assert.Equal("INVALID_REQUEST", error.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TransferRoute_DelegatesWithNormalizedReason()
        {
            var store = Guid.NewGuid();
            var expected = new RouteTransferResultDto();
            _repository.Setup(r => r.TransferRouteByOperatorAsync(store, 1, 7, 8, "motoboy passou mal")).ReturnsAsync(expected);
            var result = await Service.TransferRouteByOperatorAsync(store, 1, 7, 8, "  motoboy passou mal  ");
            Assert.Same(expected, result);
        }

        [Fact]
        public async Task CompleteStop_RejectsInvalidPedidoId()
        {
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.CompleteStopAsync(Guid.NewGuid(), 1, 0));
            Assert.Equal("INVALID_REQUEST", error.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task CompleteStop_DelegatesToRepository()
        {
            var store = Guid.NewGuid();
            var expected = new MotoboyQueueDto { MotoboyId = 7 };
            _repository.Setup(r => r.CompleteStopAsync(store, 1, 23)).ReturnsAsync(expected);
            Assert.Same(expected, await Service.CompleteStopAsync(store, 1, 23));
        }

        [Fact]
        public async Task RemoveForSimulator_KeepsTheLegacyPath()
        {
            var store = Guid.NewGuid();
            _repository.Setup(r => r.RemoveForSimulatorAsync(store, 1, 23)).ReturnsAsync(new MotoboyQueueDto());
            await Service.RemoveForSimulatorAsync(store, 1, 23);
            _repository.Verify(r => r.RemoveAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [Theory]
        [InlineData(nameof(DeliveryOrdersV2Controller.CompletePedido))]
        [InlineData(nameof(DeliveryOrdersV2Controller.TransferRoute))]
        [InlineData(nameof(DeliveryOrdersV2Controller.Remove))]
        public void OperatorQueueActions_RequireTheDeliveryModuleAndTheAssignPermission(string action)
        {
            var method = typeof(DeliveryOrdersV2Controller).GetMethod(action)!;
            Assert.Contains(method.GetCustomAttributes<RequireModuleAttribute>(), a => a.Modulo == "Delivery");
            Assert.Contains(method.GetCustomAttributes<RequirePermissionAttribute>(), a => a.Modulo == "Delivery" && a.Acao == "atribuir_motoboy");
        }
    }
}
