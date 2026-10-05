using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class AtendenteConfirmacaoServiceTests
    {
        private readonly Mock<IRastreioRepository> _rastreio = new();
        private readonly Mock<IAtendimentoRepository> _atendimento = new();
        private readonly Mock<ITrackingNoticeSender> _sender = new();
        private readonly List<(long Id, string Status, string? Motivo)> _marks = new();
        private long _nextId = 1;

        private AtendenteConfirmacaoService Service(NoticeSettings? settings = null)
        {
            _rastreio.Setup(r => r.GetSettingsAsync(It.IsAny<Guid>())).ReturnsAsync(settings ?? new NoticeSettings());
            _rastreio.Setup(r => r.TryReserveAsync(It.IsAny<int>(), NoticeTypes.ConfirmacaoAtendente)).ReturnsAsync(() => _nextId++);
            _rastreio.Setup(r => r.MarkAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>()))
                .Callback<long, string, string?, Guid?>((id, status, motivo, _) => _marks.Add((id, status, motivo)))
                .Returns(Task.CompletedTask);
            _atendimento.Setup(a => a.AbrirConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ReturnsAsync(new ConversaDoPedidoDto { ConversaId = Guid.NewGuid(), JanelaAberta = true });
            _atendimento.Setup(a => a.GetPedidoVariablesAsync(It.IsAny<Guid>(), It.IsAny<int?>()))
                .ReturnsAsync(new Dictionary<string, string?> { ["cliente"] = "Maria", ["loja"] = "Sabor", ["itens"] = "2x X-Bacon", ["total"] = "R$ 64,90", ["endereco"] = "Rua das Flores, 120" });
            _sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync(Guid.NewGuid());
            return new AtendenteConfirmacaoService(_rastreio.Object, _atendimento.Object, _sender.Object, NullLogger<AtendenteConfirmacaoService>.Instance);
        }

        [Fact]
        public async Task Sends_the_rendered_confirmation_and_marks_it_sent()
        {
            var service = Service();

            await service.TrySendAsync(Guid.NewGuid(), 55);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.Is<string>(t => t.Contains("Maria") && t.Contains("X-Bacon"))), Times.Once);
            Assert.Contains(_marks, m => m.Status == "enviada");
        }

        [Fact]
        public async Task Never_throws_and_skips_when_disabled()
        {
            var service = Service(new NoticeSettings { ConfirmacaoAtendenteEnabled = false });

            await service.TrySendAsync(Guid.NewGuid(), 55);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            _rastreio.Verify(r => r.TryReserveAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Records_failure_without_throwing_when_window_is_closed()
        {
            var service = Service();
            _atendimento.Setup(a => a.AbrirConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ReturnsAsync(new ConversaDoPedidoDto { ConversaId = Guid.NewGuid(), JanelaAberta = false });

            await service.TrySendAsync(Guid.NewGuid(), 55);

            Assert.Contains(_marks, m => m.Status == "falhou" && m.Motivo == "fora_da_janela_sem_template");
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Swallows_any_exception_so_order_creation_never_fails_because_of_it()
        {
            var service = Service();
            _atendimento.Setup(a => a.AbrirConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("sem telefone"));

            var ex = await Record.ExceptionAsync(() => service.TrySendAsync(Guid.NewGuid(), 55));

            Assert.Null(ex);
        }

        [Fact]
        public async Task Already_sent_is_not_sent_again()
        {
            var service = Service();
            _rastreio.Setup(r => r.TryReserveAsync(It.IsAny<int>(), NoticeTypes.ConfirmacaoAtendente)).ReturnsAsync((long?)null);

            await service.TrySendAsync(Guid.NewGuid(), 55);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }
    }
}
