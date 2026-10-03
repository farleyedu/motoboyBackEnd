using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class ChatRealtimePublisherTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Conversa = Guid.NewGuid();

        private sealed class Fixture
        {
            public Mock<IHubContext<DeliveryHub>> Hub { get; } = new();
            public Mock<IHubClients> Clients { get; } = new();
            public Mock<IClientProxy> Grupo { get; } = new();
            public Mock<IConversationRepository> Conversas { get; } = new();
            public string? GrupoUsado { get; private set; }
            public List<(string Evento, object?[] Args)> Enviados { get; } = new();

            public Fixture()
            {
                Hub.SetupGet(h => h.Clients).Returns(Clients.Object);
                Clients.Setup(c => c.Group(It.IsAny<string>())).Callback<string>(g => GrupoUsado = g).Returns(Grupo.Object);
                Grupo.Setup(g => g.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object?[], CancellationToken>((e, a, _) => Enviados.Add((e, a)))
                    .Returns(Task.CompletedTask);
                Conversas.Setup(c => c.ObterPorIdAsync(Conversa, null)).ReturnsAsync(new Conversation { IdConversa = Conversa, IdEstabelecimento = Loja });
            }

            public ChatRealtimePublisher Build() => new(Hub.Object, Conversas.Object, NullLogger<ChatRealtimePublisher>.Instance);
        }

        [Fact]
        public async Task A_mensagem_nova_vai_para_o_grupo_de_chat_da_loja_com_o_texto_a_direcao_e_o_horario()
        {
            var f = new Fixture();
            var mensagem = new Message
            {
                Id = Guid.NewGuid(), IdConversa = Conversa, Direcao = DirecaoMensagem.Entrada, Conteudo = "oi, quero o cardapio",
                IdMensagemWa = "wamid.AAA", CriadaPor = "cliente", Tipo = "texto", DataHora = DateTime.UtcNow
            };

            await f.Build().MensagemCriadaAsync(mensagem);

            Assert.Equal(ChatRealtimeEvents.Group(Loja), f.GrupoUsado);
            var (evento, args) = Assert.Single(f.Enviados);
            Assert.Equal("chat.message.created", evento);
            var json = System.Text.Json.JsonSerializer.Serialize(args[0]);
            Assert.Contains("\"direction\":\"in\"", json);
            Assert.Contains("oi, quero o cardapio", json);
            Assert.Contains(Conversa.ToString(), json);
        }

        [Fact]
        public async Task O_grupo_de_chat_e_diferente_do_grupo_do_delivery()
        {
            Assert.NotEqual(DeliveryRealtimeEvents.EstablishmentGroup(Loja), ChatRealtimeEvents.Group(Loja));
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Recibo_e_acao_na_conversa_tem_eventos_proprios()
        {
            var f = new Fixture();
            var publicador = f.Build();

            await publicador.StatusDaMensagemAsync(Conversa, null, "wamid.BBB", "entregue", null);
            await publicador.ConversaAtualizadaAsync(Conversa, Loja, "acao");

            Assert.Equal(new[] { "chat.message.status", "chat.conversation.updated" }, f.Enviados.Select(e => e.Evento).ToArray());
        }

        [Fact]
        public async Task Conversa_desconhecida_nao_publica_nada_e_erro_do_hub_nunca_sobe()
        {
            var f = new Fixture();
            await f.Build().StatusDaMensagemAsync(Guid.NewGuid(), null, "x", "lida", null);
            Assert.Empty(f.Enviados);

            f.Grupo.Setup(g => g.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("hub fora"));
            await f.Build().ConversaAtualizadaAsync(Conversa, Loja, "acao"); // nao pode lancar
        }
    }

    public class MessageServiceTempoRealTests
    {
        private static MessageService Build(Mock<IMessageRepository> repo, Mock<IChatRealtimePublisher> publicador) =>
            new(repo.Object, NullLogger<MessageService>.Instance, new ConfigurationBuilder().Build(), publicador.Object);

        [Fact]
        public async Task Mensagem_gravada_avisa_as_telas_e_duplicada_nao_avisa()
        {
            var repo = new Mock<IMessageRepository>();
            var publicador = new Mock<IChatRealtimePublisher>();
            var service = Build(repo, publicador);
            var mensagem = new Message { IdConversa = Guid.NewGuid(), IdMensagemWa = "wamid.AAA", Conteudo = "oi" };

            await service.AdicionarMensagemAsync(mensagem, null, "5534999990000");
            publicador.Verify(p => p.MensagemCriadaAsync(mensagem), Times.Once);

            repo.Setup(r => r.ExistsByProviderIdAsync("wamid.AAA")).ReturnsAsync(true);
            Assert.Null(await service.AdicionarMensagemAsync(mensagem, null, "5534999990000"));
            publicador.Verify(p => p.MensagemCriadaAsync(It.IsAny<Message>()), Times.Once);
        }

        [Fact]
        public async Task Recibo_aplicado_avisa_as_telas_com_a_conversa_da_mensagem()
        {
            var conversa = Guid.NewGuid();
            var repo = new Mock<IMessageRepository>();
            repo.Setup(r => r.AtualizarStatusPorProvedorAsync("wamid.BBB", "delivered", null, null)).ReturnsAsync(true);
            repo.Setup(r => r.ObterConversaPorProvedorAsync("wamid.BBB")).ReturnsAsync(conversa);
            var publicador = new Mock<IChatRealtimePublisher>();

            var atualizada = await Build(repo, publicador).AtualizarStatusPorProvedorAsync("wamid.BBB", "delivered");

            Assert.True(atualizada);
            publicador.Verify(p => p.StatusDaMensagemAsync(conversa, null, "wamid.BBB", "entregue", null), Times.Once);
        }

        [Fact]
        public async Task Recibo_de_mensagem_desconhecida_nao_avisa_ninguem()
        {
            var repo = new Mock<IMessageRepository>();
            repo.Setup(r => r.AtualizarStatusPorProvedorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(false);
            var publicador = new Mock<IChatRealtimePublisher>();

            Assert.False(await Build(repo, publicador).AtualizarStatusPorProvedorAsync("wamid.ZZZ", "read"));

            publicador.VerifyNoOtherCalls();
        }
    }
}
