// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Infra;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Controllers
{
    /// <summary>
    /// Porta de entrada do WhatsApp. So faz tres coisas, nesta ordem: conferir a assinatura, GRAVAR cada mensagem/status
    /// em wa_evento e responder. O processamento acontece depois, no WaEventoWorker: assim nenhuma mensagem se perde
    /// se o servidor reiniciar, e a Meta recebe a resposta rapido.
    /// </summary>
    [ApiController]
    [Route("wa")]
    public class WaWebhookController : ControllerBase
    {
        private readonly ILogger<WaWebhookController> _logger;
        private readonly WebhookValidatorService _validator;
        private readonly IWaWebhookIngestor _ingestor;
        private readonly IOptions<AutomationOptions> _opcoes;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            WebhookValidatorService validator,
            IWaWebhookIngestor ingestor,
            IOptions<AutomationOptions> opcoes)
        {
            _logger = logger;
            _validator = validator;
            _ingestor = ingestor;
            _opcoes = opcoes;
        }

        [HttpGet("webhook")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            var configurado = _opcoes.Value?.VerifyToken;
            if (!WebhookSignatureValidator.SegredoConfigurado(configurado))
            {
                _logger.LogError("[wa.in] ev=verificacao_recusada motivo=verify_token_nao_configurado dica=\"configure Automation__VerifyToken\"");
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = "Webhook sem token de verificacao configurado." });
            }

            if (mode == "subscribe" && string.Equals(token, configurado, StringComparison.Ordinal))
            {
                _logger.LogInformation("[wa.in] ev=verificacao_ok");
                return Ok(challenge);
            }

            _logger.LogWarning("[wa.in] ev=verificacao_recusada motivo=token_invalido");
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = "Nao autorizado." });
        }

        [HttpPost("webhook")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> Webhook()
        {
            string corpo;
            try
            {
                corpo = await _validator.ReadBodyAsync(Request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[wa.in] ev=erro motivo=corpo_ilegivel");
                return BadRequest();
            }

            if (!_validator.ValidateSignature(Request.Headers["X-Hub-Signature-256"].ToString(), corpo))
            {
                // 403 (e nao 200): mensagem sem assinatura valida nao pode ser aceita nem fingir que foi.
                _logger.LogWarning(
                    "[wa.in] ev=recusada motivo=assinatura_invalida bytes={Bytes} dica=\"confira Automation__Meta__AppSecret\"", corpo.Length);
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            JsonDocument documento;
            try
            {
                documento = JsonDocument.Parse(corpo);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[wa.in] ev=ignorada motivo=json_invalido bytes={Bytes}", corpo.Length);
                return Ok();
            }

            using (documento)
            {
                int novos = 0, repetidos = 0;
                try
                {
                    var gravados = await _ingestor.IngerirAsync(documento.RootElement, canal: null);
                    novos = gravados.Novos;
                    repetidos = gravados.Repetidos;
                }
                catch (Exception ex)
                {
                    // Sem gravar nao ha como garantir a mensagem: 500 faz a Meta reenviar depois.
                    _logger.LogError(ex, "[wa.in] ev=erro motivo=falha_ao_gravar_evento");
                    return StatusCode(StatusCodes.Status500InternalServerError);
                }

                if (novos > 0 || repetidos > 0)
                {
                    _logger.LogInformation("[wa.in] ev=lote novos={Novos} repetidos={Repetidos}", novos, repetidos);
                }
            }

            return Ok();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
