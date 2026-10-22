using System;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Controllers
{
    /// <summary>
    /// Webhook do numero PROPRIO de uma loja: cada canal tem a sua URL (/wa/webhook/{chave}), o seu verify token e o app secret do
    /// app do cliente. Mesmo desenho do webhook global: conferir a assinatura, GRAVAR em wa_evento e responder; o resto e do worker.
    /// O webhook global (/wa/webhook) nao muda e continua atendendo os numeros zippy e central.
    /// </summary>
    [ApiController]
    [Route("wa/webhook/{chave}")]
    public class WaCanalWebhookController : ControllerBase
    {
        private readonly ICanalWebhookService _webhook;
        private readonly WebhookValidatorService _validator;
        private readonly IWaWebhookIngestor _ingestor;
        private readonly ILogger<WaCanalWebhookController> _logger;

        public WaCanalWebhookController(
            ICanalWebhookService webhook, WebhookValidatorService validator, IWaWebhookIngestor ingestor, ILogger<WaCanalWebhookController> logger)
        {
            _webhook = webhook;
            _validator = validator;
            _ingestor = ingestor;
            _logger = logger;
        }

        /// <summary>Handshake da Meta ao registrar a URL: devolve o desafio se o verify token e o deste canal.</summary>
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Verificar(
            string chave,
            [FromQuery(Name = "hub.mode")] string? mode,
            [FromQuery(Name = "hub.verify_token")] string? token,
            [FromQuery(Name = "hub.challenge")] string? challenge)
        {
            var desafio = await _webhook.ResponderVerificacaoAsync(chave, mode, token, challenge);
            if (desafio == null)
            {
                // Mesma resposta para chave desconhecida e token errado: nao ajuda quem tenta adivinhar chaves.
                _logger.LogWarning("[wa.in] ev=verificacao_recusada via=canal motivo=chave_ou_token_invalido");
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = "Nao autorizado." });
            }

            return Content(desafio, "text/plain");
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> Receber(string chave)
        {
            string corpo;
            try
            {
                corpo = await _validator.ReadBodyAsync(Request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[wa.in] ev=erro via=canal motivo=corpo_ilegivel");
                return BadRequest();
            }

            var canal = await _webhook.ObterPorChaveAsync(chave);
            if (canal == null)
            {
                _logger.LogWarning("[wa.in] ev=recusada via=canal motivo=chave_desconhecida bytes={Bytes}", corpo.Length);
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            // A assinatura e SEMPRE exigida aqui, com o app secret do cliente; nao existe modo "aceita sem assinatura".
            if (!_webhook.AssinaturaValida(canal, Request.Headers["X-Hub-Signature-256"].ToString(), corpo))
            {
                _logger.LogWarning(
                    "[wa.in] ev=recusada via=canal motivo=assinatura_invalida canal={Canal} loja={Loja} bytes={Bytes} " +
                    "dica=\"o app secret do canal nao confere com o do app que enviou\"", canal.Id, canal.IdEstabelecimento, corpo.Length);
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            JsonDocument documento;
            try
            {
                documento = JsonDocument.Parse(corpo);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[wa.in] ev=ignorada via=canal motivo=json_invalido canal={Canal} bytes={Bytes}", canal.Id, corpo.Length);
                return Ok();
            }

            using (documento)
            {
                ResultadoIngestao gravados;
                try
                {
                    gravados = await _ingestor.IngerirAsync(documento.RootElement, canal);
                }
                catch (Exception ex)
                {
                    // Sem gravar nao ha como garantir a mensagem: 500 faz a Meta reenviar depois.
                    _logger.LogError(ex, "[wa.in] ev=erro via=canal motivo=falha_ao_gravar_evento canal={Canal}", canal.Id);
                    return StatusCode(StatusCodes.Status500InternalServerError);
                }

                if (gravados.Novos > 0 || gravados.Repetidos > 0 || gravados.Ignorados > 0)
                {
                    _logger.LogInformation(
                        "[wa.in] ev=lote via=canal canal={Canal} novos={Novos} repetidos={Repetidos} ignorados={Ignorados}",
                        canal.Id, gravados.Novos, gravados.Repetidos, gravados.Ignorados);
                }
            }

            return Ok();
        }
    }
}
