// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("wa")]
    public class WaWebhookController : ControllerBase
    {
        private readonly ILogger<WaWebhookController> _logger;
        private readonly WebhookValidatorService _validator;
        private readonly ConversationProcessor _conversationProcessor;
        private readonly IAResponseHandler _iaResponseHandler;
        private readonly IAssistantService? _assistant;
        private readonly IOptions<AutomationOptions> _opcoes;
        private readonly IWhatsAppTokenProvider _waTokenProvider;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            WebhookValidatorService validator,
            ConversationProcessor conversationProcessor,
            IAResponseHandler iaResponseHandler,
            IAssistantService? assistant,
            IOptions<AutomationOptions> opcoes,
            IWhatsAppTokenProvider waTokenProvider)
        {
            _logger = logger;
            _validator = validator;
            _conversationProcessor = conversationProcessor;
            _iaResponseHandler = iaResponseHandler;
            _assistant = assistant;
            _opcoes = opcoes;
            _waTokenProvider = waTokenProvider;
        }

        [HttpGet("webhook")]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            var verifyToken = _opcoes.Value?.VerifyToken ?? "zippygo123";
            if (mode == "subscribe" && token == verifyToken)
            {
                _logger.LogInformation("Webhook verificado com sucesso pelo Meta.");
                return Ok(challenge);
            }

            _logger.LogWarning("Falha na verifica��o do webhook. Token inv�lido.");
            return Forbid();
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            string payload;
            try
            {
                payload = await _validator.ReadBodyAsync(Request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao ler payload do webhook");
                return Ok();
            }

            var assinatura = Request.Headers["X-Hub-Signature-256"].ToString();
            if (!_validator.ValidateSignature(assinatura, payload))
            {
                return Ok();
            }

            WebhookPayloadDto? carga;
            try
            {
                carga = JsonSerializer.Deserialize<WebhookPayloadDto>(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payload inv�lido recebido do WhatsApp");
                return Ok();
            }

            if (carga?.Entradas == null)
            {
                return Ok();
            }

            foreach (var entrada in carga.Entradas)
            {
                if (entrada.Mudancas == null) continue;

                foreach (var mudanca in entrada.Mudancas)
                {
                    var valor = mudanca.Valor;
                    if (valor?.Mensagens == null) continue;

                    foreach (var mensagem in valor.Mensagens)
                    {
                        try
                        {
                            if (mensagem?.Texto?.Corpo == null || string.IsNullOrWhiteSpace(mensagem.Id) || string.IsNullOrWhiteSpace(mensagem.De))
                            {
                                continue;
                            }

                            DateTime? dataMsgUtc = null;
                            if (!string.IsNullOrWhiteSpace(mensagem.CarimboTempo) && long.TryParse(mensagem.CarimboTempo, out var unix))
                            {
                                try
                                {
                                    dataMsgUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                                }
                                catch
                                {
                                    dataMsgUtc = null;
                                }
                            }

                            var input = new ConversationProcessingInput(
                                Mensagem: mensagem,
                                Texto: mensagem.Texto.Corpo,
                                PhoneNumberDisplay: valor.Metadados?.NumeroTelefoneExibicao,
                                PhoneNumberId: valor.Metadados?.IdNumeroTelefone,
                                DataMensagemUtc: dataMsgUtc,
                                Valor: valor);

                            var processamento = await _conversationProcessor.ProcessAsync(input);
                            if (processamento.ShouldIgnore)
                            {
                                _logger.LogInformation("Mensagem ignorada pelo ConversationProcessor (from={From})", mensagem.De);
                                continue;
                            }

                            var idConversa = processamento.IdConversa ?? Guid.Empty;
                            var stopwatch = Stopwatch.StartNew();
                            var decision = _assistant != null
                                ? await _assistant.GerarDecisaoComHistoricoAsync(
                                    idConversa,
                                    processamento.TextoUsuario,
                                    processamento.Historico,
                                    processamento.Contexto)
                                : new AssistantDecision(
                                    Reply: processamento.TextoUsuario,
                                    HandoverAction: "none",
                                    AgentPrompt: null,
                                    ReservaConfirmada: false,
                                    Detalhes: null);
                            stopwatch.Stop();
                            _logger.LogInformation("[Conversa={Conversa}] Latencia IA: {Latency} ms", idConversa, stopwatch.ElapsedMilliseconds);

                            await _iaResponseHandler.HandleAsync(decision, processamento);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro ao processar mensagem individual {MensagemId} de {De}", mensagem?.Id, mensagem?.De);
                        }
                    }
                }
            }

            return Ok();
        }

        [HttpPost("token")]
        public IActionResult AtualizarAccessToken([FromBody] UpdateWhatsAppTokenRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.AccessToken))
            {
                return BadRequest(new { error = "AccessToken obrigat�rio" });
            }

            _waTokenProvider.SetAccessToken(req.AccessToken);
            _logger.LogInformation("Token do WhatsApp atualizado via endpoint em {When}", DateTimeOffset.UtcNow);

            return Ok(new
            {
                message = "Token atualizado com sucesso (apenas em mem�ria).",
                updated_at_utc = _waTokenProvider.LastUpdatedUtc?.ToString("o")
            });
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


