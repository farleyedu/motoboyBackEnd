using System;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>
    /// Eventos de chat em tempo real (SignalR, mesmo hub do delivery). Vao para um grupo proprio por estabelecimento, so
    /// com quem tem permissao de WhatsApp/chat: quem so enxerga o delivery nao recebe o texto das conversas.
    /// </summary>
    public static class ChatRealtimeEvents
    {
        /// <summary>Chegou ou foi enviada uma mensagem na conversa.</summary>
        public const string MessageCreated = "chat.message.created";
        /// <summary>Recibo da Meta: a mensagem enviada foi entregue, lida ou falhou.</summary>
        public const string MessageStatus = "chat.message.status";
        /// <summary>A conversa mudou (assumida, devolvida ao bot, fechada, reaberta, lida...).</summary>
        public const string ConversationUpdated = "chat.conversation.updated";

        public static string Group(Guid estabelecimentoId) => $"chat:{estabelecimentoId:N}";
    }

    public interface IChatRealtimePublisher
    {
        Task MensagemCriadaAsync(Message mensagem);
        Task StatusDaMensagemAsync(Guid idConversa, Guid? idMensagem, string? idProvedor, string status, string? erro);
        Task ConversaAtualizadaAsync(Guid idConversa, Guid idEstabelecimento, string motivo);
    }

    /// <summary>Melhor esforco: uma falha aqui nunca derruba o recebimento nem o envio; a tela tem a consulta periodica como reserva.</summary>
    public sealed class ChatRealtimePublisher : IChatRealtimePublisher
    {
        private readonly IHubContext<DeliveryHub> _hub;
        private readonly IConversationRepository _conversas;
        private readonly ILogger<ChatRealtimePublisher> _logger;

        public ChatRealtimePublisher(IHubContext<DeliveryHub> hub, IConversationRepository conversas, ILogger<ChatRealtimePublisher> logger)
        {
            _hub = hub;
            _conversas = conversas;
            _logger = logger;
        }

        public async Task MensagemCriadaAsync(Message mensagem)
        {
            await PublicarAsync(mensagem.IdConversa, null, ChatRealtimeEvents.MessageCreated, loja => new
            {
                conversationId = mensagem.IdConversa,
                estabelecimentoId = loja,
                messageId = mensagem.Id,
                providerId = string.IsNullOrWhiteSpace(mensagem.IdProvedor) ? mensagem.IdMensagemWa : mensagem.IdProvedor,
                direction = mensagem.Direcao == DirecaoMensagem.Entrada ? "in" : "out",
                type = mensagem.Tipo,
                text = Cortar(mensagem.Conteudo, 4000),
                createdBy = mensagem.CriadaPor,
                createdAt = DateTime.SpecifyKind(mensagem.DataHora, DateTimeKind.Utc),
                status = mensagem.Status
            });
        }

        public Task StatusDaMensagemAsync(Guid idConversa, Guid? idMensagem, string? idProvedor, string status, string? erro) =>
            PublicarAsync(idConversa, null, ChatRealtimeEvents.MessageStatus, loja => new
            {
                conversationId = idConversa,
                estabelecimentoId = loja,
                messageId = idMensagem,
                providerId = idProvedor,
                status,
                error = erro
            });

        public Task ConversaAtualizadaAsync(Guid idConversa, Guid idEstabelecimento, string motivo) =>
            PublicarAsync(idConversa, idEstabelecimento, ChatRealtimeEvents.ConversationUpdated, loja => new
            {
                conversationId = idConversa,
                estabelecimentoId = loja,
                reason = motivo
            });

        private async Task PublicarAsync(Guid idConversa, Guid? lojaConhecida, string evento, Func<Guid, object> payload)
        {
            try
            {
                var loja = lojaConhecida ?? (await _conversas.ObterPorIdAsync(idConversa))?.IdEstabelecimento;
                if (loja == null || loja == Guid.Empty)
                {
                    _logger.LogWarning("[chat.rt] ev=sem_loja conversa={Conversa} evento={Evento}", idConversa, evento);
                    return;
                }

                await _hub.Clients.Group(ChatRealtimeEvents.Group(loja.Value)).SendAsync(evento, payload(loja.Value));
                _logger.LogDebug("[chat.rt] ev=publicado evento={Evento} conversa={Conversa} loja={Loja}", evento, idConversa, loja);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[chat.rt] ev=erro evento={Evento} conversa={Conversa}", evento, idConversa);
            }
        }

        private static string Cortar(string? texto, int max) =>
            string.IsNullOrEmpty(texto) ? string.Empty : texto.Length <= max ? texto : texto[..max];
    }
}
