using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class MotoboyClientChatTests
    {
        [Fact]
        public async Task PedidoDeOutroMotoboyNaoAbreHistorico()
        {
            var est = Guid.NewGuid(); var queue = new Mock<IPedidoQueueService>();
            var atendimento = new Mock<IAtendimentoRepository>(); var conversation = new Mock<IConversationRepository>();
            queue.Setup(q => q.GetQueueAsync(est, 7)).ReturnsAsync(new MotoboyQueueDto { Current = new RouteStopDto { PedidoId = 23 } });
            var service = new AtendimentoService(atendimento.Object, queue.Object, null!, conversation.Object);
            await Assert.ThrowsAsync<DeliveryDomainException>(() => service.ListClientMessagesAsync(est, 7, 24, null, 50));
            atendimento.Verify(q => q.GetCanalDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
            conversation.Verify(q => q.ObterHistoricoConversaAsync(It.IsAny<Guid>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
        }

        [Fact]
        public async Task ClienteSemConversaRecebeMotivoSemCriarConversa()
        {
            var est = Guid.NewGuid(); var queue = new Mock<IPedidoQueueService>();
            var atendimento = new Mock<IAtendimentoRepository>(); var conversation = new Mock<IConversationRepository>();
            queue.Setup(q => q.GetQueueAsync(est, 7)).ReturnsAsync(new MotoboyQueueDto { Next = new List<RouteStopDto> { new() { PedidoId = 24 } } });
            atendimento.Setup(q => q.GetCanalDoPedidoAsync(est, 24)).ReturnsAsync(new PedidoCanalDto { PedidoId = 24, PodeReceber = false, Motivo = "sem_conversa" });
            var service = new AtendimentoService(atendimento.Object, queue.Object, null!, conversation.Object);
            var result = await service.ListClientMessagesAsync(est, 7, 24, null, 50);
            Assert.Empty(result.Messages); Assert.Equal("sem_conversa", result.Channel.Motivo);
            conversation.Verify(q => q.CriarNovaConversaAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        }
    }
}
