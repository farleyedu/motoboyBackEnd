using System.Collections.Generic;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class AtendimentoMotoboyClienteTests
    {
        [Fact]
        public void ValidateClientMessageText_RejectsEmpty()
        {
            Assert.Throws<DeliveryDomainException>(() => AtendimentoService.ValidateClientMessageText(null));
            Assert.Throws<DeliveryDomainException>(() => AtendimentoService.ValidateClientMessageText("   "));
        }

        [Fact]
        public void ValidateClientMessageText_RejectsTooLong()
        {
            var texto = new string('a', 1001);
            Assert.Throws<DeliveryDomainException>(() => AtendimentoService.ValidateClientMessageText(texto));
        }

        [Fact]
        public void ValidateClientMessageText_TrimsAndAcceptsValid()
        {
            Assert.Equal("Chegando em 5 minutos", AtendimentoService.ValidateClientMessageText("  Chegando em 5 minutos  "));
        }

        [Fact]
        public void PedidoEstaNaFila_DetectaAtualENaFila()
        {
            var queue = new MotoboyQueueDto
            {
                Current = new RouteStopDto { PedidoId = 10 },
                Next = new List<RouteStopDto> { new() { PedidoId = 11 }, new() { PedidoId = 12 } },
            };

            Assert.True(AtendimentoService.PedidoEstaNaFila(queue, 10));
            Assert.True(AtendimentoService.PedidoEstaNaFila(queue, 11));
            Assert.True(AtendimentoService.PedidoEstaNaFila(queue, 12));
            Assert.False(AtendimentoService.PedidoEstaNaFila(queue, 99));
        }

        [Fact]
        public void PedidoEstaNaFila_SemAtualNemProximos()
        {
            var queue = new MotoboyQueueDto();
            Assert.False(AtendimentoService.PedidoEstaNaFila(queue, 1));
        }

        [Theory]
        [InlineData(null, "motoboy")]
        [InlineData("", "motoboy")]
        [InlineData("  ", "motoboy")]
        [InlineData("João Motoboy", "Motoboy João Motoboy")]
        [InlineData("  Maria  ", "Motoboy Maria")]
        public void BuildCriadaPorLabel_UsaNomeOuRotuloGenerico(string? nome, string esperado)
        {
            Assert.Equal(esperado, AtendimentoService.BuildCriadaPorLabel(nome));
        }
    }
}
