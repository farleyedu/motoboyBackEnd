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
        /// 
        /// IMPORTANTE: Com a arquitetura de Tools, as ações (confirmar reserva, escalar para humano)
        /// já foram executadas pelo ToolExecutorService. Este handler apenas:
        /// 1. Persiste a mensagem de resposta no banco
        /// 2. Envia a mensagem para o WhatsApp
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


            // Validar se temos os dados necessários para enviar
            if (string.IsNullOrWhiteSpace(phoneNumberDisplay) || string.IsNullOrWhiteSpace(numeroDestino))
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Não é possível enviar resposta: phoneNumberDisplay ou numeroDestino ausente",
                    idConversa);
                return;
            }

            // Se a IA não gerou resposta, não há nada a fazer
            if (string.IsNullOrWhiteSpace(decision.Reply))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] IA não retornou mensagem de resposta (possivelmente apenas executou uma tool)",
                    idConversa);
                return;
            }

            // Enviar a resposta da IA para o cliente
            await EnviarMensagemAoClienteAsync(idConversa, phoneNumberDisplay, numeroDestino, phoneNumberId, decision.Reply);

            _logger.LogInformation(
                "[Conversa={Conversa}] Resposta da IA processada e enviada com sucesso",
                idConversa);
        }

        private async Task EnviarMensagemAoClienteAsync(Guid idConversa, string phoneNumberDisplay, string numeroDestino, string phoneNumberId, string texto)
        {
            try
            {
                // 🔑 Corrigir texto com caracteres escapados
                string textoLimpo;
                try
                {
                    // Tenta decodificar como JSON string escapada
                    textoLimpo = System.Text.Json.JsonSerializer.Deserialize<string>($"\"{texto}\"") ?? texto;
                }
                catch
                {
                    // Se falhar, usa o texto original
                    textoLimpo = texto;
                }

                // 1. Criar a mensagem
                var mensagem = MessageFactory.CreateMessage(
                    idConversa,
                    textoLimpo,
                    DirecaoMensagem.Saida,
                    "ia",
                    tipoOrigem: "text");

                // 2. Persistir no banco
                await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberDisplay, numeroDestino);

                _logger.LogInformation(
                    "[Conversa={Conversa}] Mensagem da IA persistida no banco: {Preview}",
                    idConversa,
                    textoLimpo.Length > 100 ? textoLimpo.Substring(0, 100) + "..." : textoLimpo);

                // 3. Publicar na fila (se aplicável)
                await _fila.PublicarSaidaAsync(mensagem);

                // 4. Enviar para o WhatsApp
                await _whatsAppSender.SendTextAsync(idConversa, phoneNumberId, numeroDestino, textoLimpo);

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