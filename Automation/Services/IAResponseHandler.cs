// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class IAResponseHandler
    {
        private const string PerguntaFallback = "Deseja que eu acione um atendente humano para te ajudar? Se sim, pode me dizer em poucas palavras o que gostaria de tratar com ele?";

        private readonly HandoverService _handoverService;
        private readonly IMessageService _mensagemService;
        private readonly IQueueBus _fila;
        private readonly WhatsAppSender _whatsAppSender;
        private readonly ILogger<IAResponseHandler> _logger;

        public IAResponseHandler(
            HandoverService handoverService,
            IMessageService mensagemService,
            IQueueBus fila,
            WhatsAppSender whatsAppSender,
            ILogger<IAResponseHandler> logger)
        {
            _handoverService = handoverService;
            _mensagemService = mensagemService;
            _fila = fila;
            _whatsAppSender = whatsAppSender;
            _logger = logger;
        }

        public async Task HandleAsync(AssistantDecision decision, ConversationProcessingResult processamento)
        {
            if (processamento.IdConversa is null || processamento.MensagemRegistrada is null)
            {
                _logger.LogWarning("[Webhook] Resultado de processamento inválido, não há conversa registrada");
                return;
            }

            var idConversa = processamento.IdConversa.Value;
            var handoverDetalhes = decision.Detalhes ?? processamento.HandoverDetalhes;
            var numeroDestino = handoverDetalhes.Telefone;
            var phoneNumberId = processamento.NumeroWhatsappId;

            switch (decision.HandoverAction.ToLowerInvariant())
            {
                case "confirm":
                    await ExecutarConfirmAsync(idConversa, decision, handoverDetalhes);
                    await EnviarMensagemAoClienteAsync(idConversa, phoneNumberId, numeroDestino, "Vou te encaminhar para um atendente humano para continuar o atendimento. Um momento, por favor.");
                    break;

                case "ask":
                    if (!string.IsNullOrWhiteSpace(decision.Reply))
                    {
                        await EnviarMensagemAoClienteAsync(idConversa, phoneNumberId, numeroDestino, decision.Reply);
                    }

                    var pergunta = decision.AgentPrompt ?? PerguntaFallback;
                    await EnviarMensagemAoClienteAsync(idConversa, phoneNumberId, numeroDestino, pergunta);
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(decision.Reply))
                    {
                        await EnviarMensagemAoClienteAsync(idConversa, phoneNumberId, numeroDestino, decision.Reply);
                    }
                    break;
            }
        }

        private async Task ExecutarConfirmAsync(Guid idConversa, AssistantDecision decision, HandoverContextDto detalhes)
        {
            var agente = new HandoverAgentDto { Id = 4, Nome = "Agente" };
            await _handoverService.ProcessarHandoverAsync(idConversa, agente, decision.ReservaConfirmada || string.Equals(decision.HandoverAction, "confirm", StringComparison.OrdinalIgnoreCase), detalhes, telegramChatIdOverride: null);
        }

        private async Task EnviarMensagemAoClienteAsync(Guid idConversa, string? phoneNumberId, string? numeroDestino, string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;
            if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(numeroDestino))
            {
                _logger.LogWarning("[Conversa={Conversa}] Não foi possível enviar mensagem ao cliente: phoneNumberId ou destino ausente", idConversa);
                return;
            }

            var mensagem = MessageFactory.CreateMessage(idConversa, texto!, DirecaoMensagem.Saida, "ia");
            await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberId, numeroDestino);
            await _fila.PublicarSaidaAsync(mensagem);

            await _whatsAppSender.SendTextAsync(idConversa, phoneNumberId, numeroDestino, texto!);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
