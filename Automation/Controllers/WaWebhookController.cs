// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Text.Json;
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
        private readonly IWebhookDispatchService _dispatcher;
        private readonly IWebhookMessageCache _messageCache;
        private readonly IOptions<AutomationOptions> _opcoes;
        private readonly IWhatsAppTokenProvider _waTokenProvider;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            WebhookValidatorService validator,
            IWebhookDispatchService dispatcher,
            IWebhookMessageCache messageCache,
            IOptions<AutomationOptions> opcoes,
            IWhatsAppTokenProvider waTokenProvider)
        {
            _logger = logger;
            _validator = validator;
            _dispatcher = dispatcher;
            _messageCache = messageCache;
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

                            if (!_messageCache.TryRegister(mensagem.Id))
                            {
                                _logger.LogInformation("[Webhook] Mensagem duplicada ignorada (id={MensagemId})", mensagem.Id);
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

                            await _dispatcher.EnqueueAsync(input, HttpContext.RequestAborted);
                            _logger.LogDebug("[Webhook] Mensagem {MensagemId} enfileirada (from={From})", mensagem.Id, mensagem.De);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Erro ao preparar mensagem {MensagemId} de {De}", mensagem?.Id, mensagem?.De);
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


