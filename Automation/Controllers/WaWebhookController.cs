// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

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
        private readonly IHostEnvironment _hostEnvironment;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            WebhookValidatorService validator,
            IWebhookDispatchService dispatcher,
            IWebhookMessageCache messageCache,
            IOptions<AutomationOptions> opcoes,
            IWhatsAppTokenProvider waTokenProvider,
            IHostEnvironment hostEnvironment)
        {
            _logger = logger;
            _validator = validator;
            _dispatcher = dispatcher;
            _messageCache = messageCache;
            _opcoes = opcoes;
            _waTokenProvider = waTokenProvider;
            _hostEnvironment = hostEnvironment;
        }

        [HttpGet("webhook")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
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
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                error = "Não autorizado."
            });
        }

        [HttpPost("webhook")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            string payload;
            var isDevelopment = _hostEnvironment.IsDevelopment();

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
            _logger.LogInformation("[Webhook] POST recebido. SignaturePresent={SignaturePresent} PayloadLength={PayloadLength} ContentType={ContentType}",
                !string.IsNullOrWhiteSpace(assinatura), payload.Length, Request.ContentType ?? "(null)");
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

            _logger.LogInformation("[Webhook] Payload valido. Entradas={Entries}", carga.Entradas.Count);

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
                            var originalMessageId = mensagem?.Id;
                            var textoExibicao = ExtrairTextoExibicao(mensagem);
                            var textoInterpretado = ExtrairTextoInterpretado(mensagem);

                            if (string.IsNullOrWhiteSpace(textoExibicao) || string.IsNullOrWhiteSpace(mensagem?.Id) || string.IsNullOrWhiteSpace(mensagem.De))
                            {
                                _logger.LogDebug("[Webhook] Mensagem ignorada por falta de campos essenciais (id={MensagemId}, from={From})", mensagem?.Id, MaskValue(mensagem?.De));
                                continue;
                            }

                            if (isDevelopment)
                            {
                                if (!_messageCache.TryRegister(mensagem.Id))
                                {
                                    var novoId = $"local-{Guid.NewGuid():N}";
                                    _logger.LogDebug("[Webhook][DEV] Mensagem duplicada detectada (id={MensagemId}); substituindo por {NovoId}", originalMessageId, novoId);
                                    mensagem.Id = novoId;
                                    _messageCache.TryRegister(mensagem.Id);
                                }
                            }
                            else
                            {
                                if (!_messageCache.TryRegister(mensagem.Id))
                                {
                                    _logger.LogInformation("[Webhook] Mensagem duplicada ignorada (id={MensagemId})", mensagem.Id);
                                    continue;
                                }
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
                                Texto: textoExibicao,
                                PhoneNumberDisplay: valor.Metadados?.NumeroTelefoneExibicao,
                                PhoneNumberId: valor.Metadados?.IdNumeroTelefone,
                                DataMensagemUtc: dataMsgUtc,
                                Valor: valor,
                                TextoInterpretado: textoInterpretado);

                            _logger.LogInformation("[Webhook] Mensagem recebida id={MensagemId} from={From} phoneNumberId={PhoneNumberId} display={Display}",
                                mensagem.Id,
                                MaskValue(mensagem.De),
                                valor.Metadados?.IdNumeroTelefone ?? "(null)",
                                valor.Metadados?.NumeroTelefoneExibicao ?? "(null)");
                            _logger.LogInformation(
                                "[Webhook] Conteudo interpretado tipo={Tipo} exibicao='{TextoExibicao}' interpretado='{TextoInterpretado}'",
                                mensagem.Tipo ?? "(null)",
                                textoExibicao ?? "(null)",
                                textoInterpretado ?? "(null)");

                            await _dispatcher.EnqueueAsync(input, HttpContext.RequestAborted);
                            _logger.LogDebug("[Webhook] Mensagem {MensagemId} enfileirada (from={From})", mensagem.Id, MaskValue(mensagem.De));
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
        [RequirePermission("WhatsApp", "configurar")]
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

        private static string MaskValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(vazio)";
            if (value.Length <= 4) return value;
            var tail = value[^4..];
            return new string('*', value.Length - 4) + tail;
        }

        private static string? ExtrairTextoExibicao(WebhookMessageDto? mensagem)
        {
            if (mensagem == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(mensagem.Texto?.Corpo))
            {
                return mensagem.Texto.Corpo;
            }

            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ButtonReply?.Title))
            {
                return mensagem.Interactive.ButtonReply.Title;
            }

            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ListReply?.Title))
            {
                return mensagem.Interactive.ListReply.Title;
            }

            return null;
        }

        private static string? ExtrairTextoInterpretado(WebhookMessageDto? mensagem)
        {
            if (mensagem == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ButtonReply?.Id))
            {
                return mensagem.Interactive.ButtonReply.Id;
            }

            if (!string.IsNullOrWhiteSpace(mensagem.Interactive?.ListReply?.Id))
            {
                return mensagem.Interactive.ListReply.Id;
            }

            return mensagem.Texto?.Corpo;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


