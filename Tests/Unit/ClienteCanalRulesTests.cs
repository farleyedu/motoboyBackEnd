using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class ClienteCanalRulesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 6, 20, 0, 0, TimeSpan.Zero);
        private static readonly Guid Conversa = Guid.NewGuid();

        [Theory]
        [InlineData("atendente")]
        [InlineData("cardapio_web")]
        [InlineData("ia_whatsapp")]
        [InlineData("simulador")]
        [InlineData(null)]
        public void Com_janela_aberta_qualquer_origem_que_nao_seja_ifood_pode_receber(string? origem)
        {
            var (pode, motivo) = ClienteCanalRules.Evaluate(origem, Conversa, Now.AddHours(3), Now);
            Assert.True(pode);
            Assert.Null(motivo);
        }

        [Theory]
        [InlineData("ifood")]
        [InlineData(" IFOOD ")]
        public void Pedido_do_ifood_nunca_recebe_mesmo_com_conversa_e_janela_aberta(string origem)
        {
            var (pode, motivo) = ClienteCanalRules.Evaluate(origem, Conversa, Now.AddHours(3), Now);
            Assert.False(pode);
            Assert.Equal(ClienteCanalRules.Ifood, motivo);
        }

        [Fact]
        public void Balcao_sem_conversa_no_whatsapp_nao_recebe()
        {
            var (pode, motivo) = ClienteCanalRules.Evaluate("atendente", null, null, Now);
            Assert.False(pode);
            Assert.Equal(ClienteCanalRules.SemConversa, motivo);
        }

        [Fact]
        public void Cliente_cadastrado_que_nunca_escreveu_nao_recebe()
        {
            var (pode, motivo) = ClienteCanalRules.Evaluate("atendente", Conversa, null, Now);
            Assert.False(pode);
            Assert.Equal(ClienteCanalRules.NuncaEscreveu, motivo);
        }

        [Fact]
        public void Janela_vencida_nao_recebe_e_o_instante_exato_do_fim_ja_conta_como_fechada()
        {
            Assert.Equal((false, ClienteCanalRules.JanelaFechada), ClienteCanalRules.Evaluate("atendente", Conversa, Now.AddMinutes(-1), Now));
            Assert.Equal((false, ClienteCanalRules.JanelaFechada), ClienteCanalRules.Evaluate("atendente", Conversa, Now, Now));
        }

        [Fact]
        public void Todo_motivo_tem_explicacao_propria()
        {
            var motivos = new[] { ClienteCanalRules.Ifood, ClienteCanalRules.SemConversa, ClienteCanalRules.NuncaEscreveu, ClienteCanalRules.JanelaFechada };
            Assert.Equal(motivos.Length, motivos.Select(ClienteCanalRules.Explain).Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(ClienteCanalRules.Explain(null)));
        }
    }

    public sealed class AvisoDespachoServiceTests
    {
        private readonly Mock<IAtendimentoRepository> _repository = new();
        private readonly Mock<IPedidoQueueService> _queue = new();
        private readonly Guid _est = Guid.NewGuid();

        // O envio em si (ConversationManagementService) so e alcancado quando o canal esta liberado; aqui so se testa o que vem antes.
        private AtendimentoService Create() => new(_repository.Object, _queue.Object, null!);

        [Fact]
        public async Task Canais_ignora_ids_invalidos_e_repetidos_e_pedido_de_outra_loja()
        {
            _repository.Setup(r => r.GetCanalDoPedidoAsync(_est, 7)).ReturnsAsync(new PedidoCanalDto { PedidoId = 7, PodeReceber = true });
            _repository.Setup(r => r.GetCanalDoPedidoAsync(_est, 8))
                .ThrowsAsync(new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento."));

            var canais = await Create().GetCanaisAsync(_est, new PedidosCanaisRequest { PedidoIds = new List<int> { 7, 7, 0, -3, 8 } });

            Assert.Single(canais);
            Assert.Equal(7, canais[0].PedidoId);
            _repository.Verify(r => r.GetCanalDoPedidoAsync(_est, 7), Times.Once);
        }

        [Fact]
        public async Task Canais_exige_ao_menos_um_pedido_e_tem_teto()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() => Create().GetCanaisAsync(_est, null));
            await Assert.ThrowsAsync<DeliveryDomainException>(() => Create().GetCanaisAsync(_est, new PedidosCanaisRequest { PedidoIds = new List<int>() }));
            var muitos = Enumerable.Range(1, AtendimentoService.MaxPedidosPorConsultaDeCanal + 1).ToList();
            await Assert.ThrowsAsync<DeliveryDomainException>(() => Create().GetCanaisAsync(_est, new PedidosCanaisRequest { PedidoIds = muitos }));
        }

        [Theory]
        [InlineData(ClienteCanalRules.Ifood)]
        [InlineData(ClienteCanalRules.NuncaEscreveu)]
        [InlineData(ClienteCanalRules.JanelaFechada)]
        [InlineData(ClienteCanalRules.SemConversa)]
        public async Task Aviso_e_recusado_no_servidor_quando_o_cliente_nao_tem_canal(string motivo)
        {
            _repository.Setup(r => r.GetCanalDoPedidoAsync(_est, 7))
                .ReturnsAsync(new PedidoCanalDto { PedidoId = 7, PodeReceber = false, Motivo = motivo, ConversaId = Guid.NewGuid() });

            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Create().SendAvisoDespachoAsync(_est, 7, new SendAvisoDespachoRequest { Mensagem = "Seu pedido saiu." }));

            Assert.Equal(409, erro.StatusCode);
            Assert.Equal("CLIENTE_SEM_CANAL", erro.Code);
        }

        [Theory]
        [InlineData(ClienteCanalRules.Ifood)]
        [InlineData(ClienteCanalRules.NuncaEscreveu)]
        [InlineData(ClienteCanalRules.JanelaFechada)]
        [InlineData(ClienteCanalRules.SemConversa)]
        public async Task Mensagem_do_motoboy_ao_cliente_sem_canal_e_recusada_sem_criar_conversa(string motivo)
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 3)).ReturnsAsync(new MotoboyQueueDto { Current = new RouteStopDto { PedidoId = 7 } });
            _repository.Setup(r => r.GetCanalDoPedidoAsync(_est, 7)).ReturnsAsync(new PedidoCanalDto { PedidoId = 7, PodeReceber = false, Motivo = motivo });

            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Create().SendToClientAsync(_est, 3, 7, new SendMotoboyClientMessageRequest { Mensagem = "Cheguei." }));

            Assert.Equal(409, erro.StatusCode);
            Assert.Equal("CLIENTE_SEM_CANAL", erro.Code);
            Assert.Equal(ClienteCanalRules.Explain(motivo), erro.Message);
            _repository.Verify(r => r.AbrirConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task Mensagem_do_motoboy_sobre_pedido_fora_da_fila_dele_nem_consulta_o_canal()
        {
            _queue.Setup(q => q.GetQueueAsync(_est, 3)).ReturnsAsync(new MotoboyQueueDto());

            var erro = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Create().SendToClientAsync(_est, 3, 7, new SendMotoboyClientMessageRequest { Mensagem = "Cheguei." }));

            Assert.Equal(403, erro.StatusCode);
            _repository.Verify(r => r.GetCanalDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task Aviso_vazio_nem_consulta_o_pedido()
        {
            await Assert.ThrowsAsync<DeliveryDomainException>(() => Create().SendAvisoDespachoAsync(_est, 7, new SendAvisoDespachoRequest { Mensagem = "  " }));
            _repository.Verify(r => r.GetCanalDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }
    }
}
