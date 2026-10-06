using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Services;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class ConversationAutoCloseServiceTests
    {
        private readonly Mock<IAtendimentoRepository> _atendimento = new();
        private readonly Mock<IRastreioRepository> _rastreio = new();
        private readonly Mock<IConversationCloser> _closer = new();
        private readonly Guid _est = Guid.NewGuid();
        private readonly Guid _conversa = Guid.NewGuid();

        private ConversationAutoCloseService Create() =>
            new(_atendimento.Object, _rastreio.Object, _closer.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConversationAutoCloseService>.Instance);

        private PedidoEntregueConversaDto Candidate(int pedidoId) => new() { PedidoId = pedidoId, EstabelecimentoId = _est, ConversaId = _conversa };

        [Fact]
        public async Task Fecha_a_conversa_de_cada_pedido_entregue_e_marca_a_reserva_como_enviada()
        {
            _atendimento.Setup(a => a.GetPedidosEntreguesComConversaAbertaAsync())
                .ReturnsAsync(new List<PedidoEntregueConversaDto> { Candidate(7) });
            _rastreio.Setup(r => r.TryReserveAsync(7, ConversationAutoCloseTypes.EntregaConcluida)).ReturnsAsync(42L);
            _closer.Setup(c => c.CloseAsync(_conversa, _est, null, "Sistema", It.IsAny<CloseConversationRequest>()))
                .ReturnsAsync(new ConversationActionResponseDto());

            var closed = await Create().RunOnceAsync();

            Assert.Equal(1, closed);
            _closer.Verify(c => c.CloseAsync(_conversa, _est, null, "Sistema", It.Is<CloseConversationRequest>(r => r!.Tipo == "manual")), Times.Once);
            _rastreio.Verify(r => r.MarkAsync(42L, "enviada", null, null), Times.Once);
        }

        [Fact]
        public async Task Pedido_ja_reservado_por_outra_passada_nao_tenta_fechar_de_novo()
        {
            _atendimento.Setup(a => a.GetPedidosEntreguesComConversaAbertaAsync())
                .ReturnsAsync(new List<PedidoEntregueConversaDto> { Candidate(7) });
            _rastreio.Setup(r => r.TryReserveAsync(7, ConversationAutoCloseTypes.EntregaConcluida)).ReturnsAsync((long?)null);

            var closed = await Create().RunOnceAsync();

            Assert.Equal(0, closed);
            _closer.Verify(c => c.CloseAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CloseConversationRequest>()), Times.Never);
        }

        [Fact]
        public async Task Conversa_ja_fechada_por_corrida_e_registrada_como_ignorada_sem_contar_como_falha()
        {
            _atendimento.Setup(a => a.GetPedidosEntreguesComConversaAbertaAsync())
                .ReturnsAsync(new List<PedidoEntregueConversaDto> { Candidate(7) });
            _rastreio.Setup(r => r.TryReserveAsync(7, ConversationAutoCloseTypes.EntregaConcluida)).ReturnsAsync(42L);
            _closer.Setup(c => c.CloseAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CloseConversationRequest>()))
                .ThrowsAsync(new ConversationManagementException(409, "Conversa encerrada. Reabra antes de continuar."));

            var closed = await Create().RunOnceAsync();

            Assert.Equal(0, closed);
            _rastreio.Verify(r => r.MarkAsync(42L, "ignorada", "conversa_ja_fechada", null), Times.Once);
        }

        [Fact]
        public async Task Erro_inesperado_fica_registrado_como_falha_e_nao_derruba_os_demais_candidatos()
        {
            _atendimento.Setup(a => a.GetPedidosEntreguesComConversaAbertaAsync())
                .ReturnsAsync(new List<PedidoEntregueConversaDto> { Candidate(7), Candidate(8) });
            _rastreio.Setup(r => r.TryReserveAsync(7, ConversationAutoCloseTypes.EntregaConcluida)).ReturnsAsync(42L);
            _rastreio.Setup(r => r.TryReserveAsync(8, ConversationAutoCloseTypes.EntregaConcluida)).ReturnsAsync(43L);
            _closer.SetupSequence(c => c.CloseAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CloseConversationRequest>()))
                .ThrowsAsync(new InvalidOperationException("falha de rede"))
                .ReturnsAsync(new ConversationActionResponseDto());

            var closed = await Create().RunOnceAsync();

            Assert.Equal(1, closed);
            _rastreio.Verify(r => r.MarkAsync(42L, "falhou", "falha de rede", null), Times.Once);
            _rastreio.Verify(r => r.MarkAsync(43L, "enviada", null, null), Times.Once);
        }

        [Fact]
        public async Task Sem_candidatos_nao_consulta_a_reserva_nem_o_fechamento()
        {
            _atendimento.Setup(a => a.GetPedidosEntreguesComConversaAbertaAsync())
                .ReturnsAsync(new List<PedidoEntregueConversaDto>());

            var closed = await Create().RunOnceAsync();

            Assert.Equal(0, closed);
            _rastreio.Verify(r => r.TryReserveAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        }
    }
}
