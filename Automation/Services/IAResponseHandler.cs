// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class IAResponseHandler
    {
        private readonly IMessageService _mensagemService;
        private readonly IQueueBus _fila;
        private readonly WhatsAppSender _whatsAppSender;
        private readonly ILogger<IAResponseHandler> _logger;

        public IAResponseHandler(
            IMessageService mensagemService,
            IQueueBus fila,
            WhatsAppSender whatsAppSender,
            ILogger<IAResponseHandler> logger)
        {
            _mensagemService = mensagemService;
            _fila = fila;
            _whatsAppSender = whatsAppSender;
            _logger = logger;
        }

        public async Task HandleAsync(AssistantDecision decision, ConversationProcessingResult processamento)
        {
            if (processamento.IdConversa is null || processamento.MensagemRegistrada is null)
            {
                _logger.LogWarning("[IAResponseHandler] Resultado de processamento invalido; nenhuma conversa associada");
                return;
            }

            var idConversa = processamento.IdConversa.Value;
            var numeroDestino = processamento.HandoverDetalhes?.Telefone;
            var phoneNumberDisplay = processamento.NumeroTelefoneExibicao;
            var phoneNumberId = processamento.NumeroWhatsappId;

            if (string.IsNullOrWhiteSpace(phoneNumberDisplay) || string.IsNullOrWhiteSpace(numeroDestino) || string.IsNullOrWhiteSpace(phoneNumberId))
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Nao foi possivel enviar resposta: informacoes de telefone ausentes",
                    idConversa);
                return;
            }

            if (decision.Media != null)
            {
                var mediaEnviada = await TentarEnviarMediaAsync(idConversa, phoneNumberId, numeroDestino, decision.Media);
                if (!mediaEnviada)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] Media informada foi ignorada por tipo nao suportado ou erro de envio",
                        idConversa);
                }
            }

            if (string.IsNullOrWhiteSpace(decision.Reply))
            {
                if (decision.Media == null)
                {
                    _logger.LogInformation(
                        "[Conversa={Conversa}] IA nao retornou mensagem de resposta",
                        idConversa);
                }
                return;
            }

            await EnviarMensagemAoClienteAsync(idConversa, phoneNumberDisplay, numeroDestino, phoneNumberId, decision.Reply);

            _logger.LogInformation(
                "[Conversa={Conversa}] Resposta da IA processada com sucesso",
                idConversa);
        }

        private async Task<bool> TentarEnviarMediaAsync(Guid idConversa, string phoneNumberId, string numeroDestino, AssistantMedia media)
        {
            try
            {
                switch (media.Tipo)
                {
                    case "imagem":
                    case "image":
                        await _whatsAppSender.SendImageAsync(idConversa, phoneNumberId, numeroDestino, media.Url);
                        return true;

                    case "pdf":
                        await _whatsAppSender.SendDocumentAsync(idConversa, phoneNumberId, numeroDestino, media.Url, "reserva.pdf");
                        return true;

                    case "link":
                        await _whatsAppSender.SendTextAsync(idConversa, phoneNumberId, numeroDestino, media.Url);
                        return true;

                    default:
                        _logger.LogWarning("[Conversa={Conversa}] Tipo de media nao suportado: {Tipo}", idConversa, media.Tipo);
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Conversa={Conversa}] Falha ao enviar media para o cliente", idConversa);
                return false;
            }
        }

        private async Task EnviarMensagemAoClienteAsync(Guid idConversa, string phoneNumberDisplay, string numeroDestino, string phoneNumberId, string texto)
        {
            try
            {
                var textoFinalParaUsuario = ExtrairTextoDeResposta(texto);

                var mensagem = MessageFactory.CreateMessage(
                    idConversa,
                    textoFinalParaUsuario,
                    DirecaoMensagem.Saida,
                    "ia",
                    tipoOrigem: "text");

                await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberDisplay, numeroDestino);
                await _fila.PublicarSaidaAsync(mensagem);
                await _whatsAppSender.SendTextAsync(idConversa, phoneNumberId, numeroDestino, textoFinalParaUsuario);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Conversa={Conversa}] Erro ao enviar mensagem ao cliente. Texto: {Texto}",
                    idConversa,
                    texto);
                throw;
            }
        }

        private static string ExtrairTextoDeResposta(string texto)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto);
                if (doc.RootElement.TryGetProperty("reply", out var replyProperty) && replyProperty.ValueKind == JsonValueKind.String)
                {
                    return replyProperty.GetString() ?? texto;
                }
            }
            catch (JsonException)
            {
            }

            return texto;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ==================
