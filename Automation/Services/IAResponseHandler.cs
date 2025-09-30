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

        /// <summary>
        /// Processa a resposta da IA e envia ao cliente via WhatsApp.
        /// </summary>
        public async Task HandleAsync(AssistantDecision decision, ConversationProcessingResult processamento)
        {
            if (processamento.IdConversa is null || processamento.MensagemRegistrada is null)
            {
                _logger.LogWarning("[IAResponseHandler] Resultado de processamento inválido, não há conversa registrada");
                return;
            }

            var idConversa = processamento.IdConversa.Value;
            var numeroDestino = processamento.HandoverDetalhes?.Telefone;
            var phoneNumberDisplay = processamento.NumeroTelefoneExibicao;
            var phoneNumberId = processamento.NumeroWhatsappId;

            if (string.IsNullOrWhiteSpace(phoneNumberDisplay) || string.IsNullOrWhiteSpace(numeroDestino))
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Não é possível enviar resposta: phoneNumberDisplay ou numeroDestino ausente",
                    idConversa);
                return;
            }

            if (string.IsNullOrWhiteSpace(decision.Reply))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] IA não retornou mensagem de resposta (possivelmente apenas executou uma tool)",
                    idConversa);
                return;
            }

            await EnviarMensagemAoClienteAsync(idConversa, phoneNumberDisplay, numeroDestino, phoneNumberId, decision.Reply);

            _logger.LogInformation(
                "[Conversa={Conversa}] Resposta da IA processada e enviada com sucesso",
                idConversa);
        }

        private async Task EnviarMensagemAoClienteAsync(Guid idConversa, string phoneNumberDisplay, string numeroDestino, string phoneNumberId, string texto)
        {
            try
            {
                // ================= CORREÇÃO APLICADA AQUI =================
                // O texto pode ser uma string simples ou um JSON vindo de uma Tool.
                // Esta lógica extrai a mensagem de "reply" se for um JSON.
                string textoFinalParaUsuario = texto;
                try
                {
                    using var doc = JsonDocument.Parse(texto);
                    if (doc.RootElement.TryGetProperty("reply", out var replyProperty) && replyProperty.ValueKind == JsonValueKind.String)
                    {
                        textoFinalParaUsuario = replyProperty.GetString() ?? texto;
                    }
                }
                catch (JsonException)
                {
                    // Se não for um JSON válido, simplesmente usamos o texto original.
                    // Isso é esperado quando a IA responde sem chamar uma tool.
                }
                // ================= FIM DA CORREÇÃO =================

                // 1. Criar a mensagem
                var mensagem = MessageFactory.CreateMessage(
                    idConversa,
                    textoFinalParaUsuario, // Usar o texto extraído
                    DirecaoMensagem.Saida,
                    "ia",
                    tipoOrigem: "text");

                // 2. Persistir no banco
                await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberDisplay, numeroDestino);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Mensagem da IA persistida no banco: {Preview}",
                    idConversa,
                    textoFinalParaUsuario.Length > 100 ? textoFinalParaUsuario.Substring(0, 100) + "..." : textoFinalParaUsuario);

                // 3. Publicar na fila (se aplicável)
                await _fila.PublicarSaidaAsync(mensagem);

                // 4. Enviar para o WhatsApp
                await _whatsAppSender.SendTextAsync(idConversa, phoneNumberId, numeroDestino, textoFinalParaUsuario); // Usar o texto extraído

                _logger.LogInformation(
                    "[Conversa={Conversa}] Mensagem enviada ao WhatsApp com sucesso",
                    idConversa);
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
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ==================
