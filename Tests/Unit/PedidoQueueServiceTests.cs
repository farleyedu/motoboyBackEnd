using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class PedidoQueueServiceTests
    {
        private readonly Mock<IPedidoQueueRepository> _repository = new();

        [Fact]
        public async Task AssignAsync_RejectsInvalidMotoboyId()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AssignAsync(Guid.NewGuid(), 1, motoboyId: 0, pedidoId: 5));

            Assert.Equal("INVALID_REQUEST", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task AssignAsync_RejectsInvalidPedidoId()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AssignAsync(Guid.NewGuid(), 1, motoboyId: 10, pedidoId: 0));

            Assert.Equal("INVALID_REQUEST", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task AssignAsync_DelegatesToRepositoryWhenValid()
        {
            var estabelecimentoId = Guid.NewGuid();
            var expected = new MotoboyQueueDto { MotoboyId = 10, EstabelecimentoId = estabelecimentoId, Version = 1 };
            _repository.Setup(r => r.AssignAsync(estabelecimentoId, 1, 10, 5)).ReturnsAsync(expected);
            var service = CreateService();

            var result = await service.AssignAsync(estabelecimentoId, 1, 10, 5);

            Assert.Same(expected, result);
            _repository.Verify(r => r.AssignAsync(estabelecimentoId, 1, 10, 5), Times.Once);
        }

        [Fact]
        public async Task ReorderAsync_RejectsEmptyList()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.ReorderAsync(Guid.NewGuid(), 1, 10, 0, Array.Empty<int>()));

            Assert.Equal("INVALID_REQUEST", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ReorderAsync_RejectsDuplicatePedidoIds()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.ReorderAsync(Guid.NewGuid(), 1, 10, 0, new List<int> { 5, 5 }));

            Assert.Equal("INVALID_REQUEST", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RemoveAsync_RejectsInvalidPedidoId()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.RemoveAsync(Guid.NewGuid(), 1, 0));

            Assert.Equal("INVALID_REQUEST", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task CancelAsync_PassesReasonThrough()
        {
            var estabelecimentoId = Guid.NewGuid();
            var expected = new MotoboyQueueDto { MotoboyId = 10, EstabelecimentoId = estabelecimentoId, Version = 2 };
            _repository.Setup(r => r.CancelAsync(estabelecimentoId, 1, 5, "cliente desistiu")).ReturnsAsync(expected);
            var service = CreateService();

            var result = await service.CancelAsync(estabelecimentoId, 1, 5, "cliente desistiu");

            Assert.Same(expected, result);
        }

        private PedidoQueueService CreateService() => new(_repository.Object);
    }
}
