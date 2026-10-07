using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class MotoboyPedidoServiceTests
    {
        private readonly Guid _est = Guid.NewGuid();
        private readonly Mock<IPedidoQueueService> _queue = new();
        private readonly Mock<IPedidoConsultaRepository> _consulta = new();
        private MotoboyPedidoService Service => new(_queue.Object, _consulta.Object);

        [Fact]
        public async Task NaoConsultaPedidoForaDaFila()
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 7)).ReturnsAsync(new MotoboyQueueDto());
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.GetAsync(_est, 7, 23));
            Assert.Equal("PEDIDO_NOT_IN_YOUR_QUEUE", error.Code);
            _consulta.Verify(q => q.GetAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task ReutilizaFotosMasNaoSerializaCodigoSecreto()
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 7)).ReturnsAsync(new MotoboyQueueDto
            { Version = 8, Current = new RouteStopDto { PedidoId = 23, Position = 1 } });
            _consulta.Setup(q => q.GetAsync(_est, 23)).ReturnsAsync(new PedidoDetalheDto
            {
                Id = 23, MotoboyId = 7, CodigoEntrega = "codigo-secreto-do-cliente",
                Itens = new List<PedidoItemDto> { new() { Nome = "Pizza", ImagemUrl = "https://catalogo.example/pizza.jpg" } }
            });
            var result = await Service.GetAsync(_est, 7, 23);
            Assert.Equal(8, result.QueueVersion);
            Assert.True(result.IsCurrent);
            Assert.True(result.RequerCodigoEntrega);
            Assert.Equal("https://catalogo.example/pizza.jpg", result.Itens[0].ImagemUrl);
            Assert.DoesNotContain("codigo-secreto-do-cliente", JsonSerializer.Serialize(result));
        }

        [Fact]
        public async Task RecusaPedidoTransferidoDuranteALeitura()
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 7)).ReturnsAsync(new MotoboyQueueDto
            { Current = new RouteStopDto { PedidoId = 23 } });
            _consulta.Setup(q => q.GetAsync(_est, 23)).ReturnsAsync(new PedidoDetalheDto { Id = 23, MotoboyId = 9 });
            await Assert.ThrowsAsync<DeliveryDomainException>(() => Service.GetAsync(_est, 7, 23));
        }

        [Fact]
        public async Task PermitePedidoNaOfertaDoProprioMotoboy()
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 7)).ReturnsAsync(new MotoboyQueueDto
            { Offer = new MotoboyOfferDto { Stops = new List<RouteStopDto> { new() { PedidoId = 23 } } } });
            _consulta.Setup(q => q.GetAsync(_est, 23)).ReturnsAsync(new PedidoDetalheDto { Id = 23, MotoboyId = 7 });
            var result = await Service.GetAsync(_est, 7, 23);
            Assert.True(result.IsOffer);
            Assert.False(result.IsCurrent);
        }
    }
}
