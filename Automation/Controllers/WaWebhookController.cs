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
        private readonly IWaEventoRepository _eventos;
        private readonly IOptions<AutomationOptions> _opcoes;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            WebhookValidatorService validator,
            IWaEventoRepository eventos,
            IOptions<AutomationOptions> opcoes)
        {
            _logger = logger;
            _validator = validator;
            _eventos = eventos;
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
                    foreach (var valor in Valores(documento.RootElement))
                    {
                        (var n, var r) = await GravarEventosAsync(valor);
                        novos += n;
                        repetidos += r;
                    }
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

        private static System.Collections.Generic.IEnumerable<JsonElement> Valores(JsonElement raiz)
        {
            if (!raiz.TryGetProperty("entry", out var entradas) || entradas.ValueKind != JsonValueKind.Array) yield break;

            foreach (var entrada in entradas.EnumerateArray())
            {
                if (!entrada.TryGetProperty("changes", out var mudancas) || mudancas.ValueKind != JsonValueKind.Array) continue;

                foreach (var mudanca in mudancas.EnumerateArray())
                {
                    if (mudanca.TryGetProperty("value", out var valor) && valor.ValueKind == JsonValueKind.Object) yield return valor;
                }
            }
        }

        private async Task<(int Novos, int Repetidos)> GravarEventosAsync(JsonElement valor)
        {
            int novos = 0, repetidos = 0;
            valor.TryGetProperty("metadata", out var metadata);
            var phoneNumberId = Texto(metadata, "phone_number_id");
            var display = Texto(metadata, "display_phone_number");
            valor.TryGetProperty("contacts", out var contatos);

            if (valor.TryGetProperty("messages", out var mensagens) && mensagens.ValueKind == JsonValueKind.Array)
            {
                foreach (var mensagem in mensagens.EnumerateArray())
                {
                    var id = Texto(mensagem, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        _logger.LogWarning("[wa.in] ev=ignorada motivo=mensagem_sem_id phone_number_id={PhoneNumberId}", phoneNumberId);
                        continue;
                    }

                    var payload = JsonSerializer.Serialize(new
                    {
                        metadata = Clonar(metadata),
                        contacts = Clonar(contatos),
                        message = Clonar(mensagem)
                    });

                    var novo = await _eventos.RegistrarAsync(new WaEvento
                    {
                        Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = id,
                        PhoneNumberId = phoneNumberId, DisplayPhone = display, PayloadJson = payload
                    });

                    _logger.LogInformation(
                        "[wa.in] ev={Evento} wa={Wa} tipo={Tipo} de={De} phone_number_id={PhoneNumberId} display={Display} bytes={Bytes}",
                        novo ? "recebida" : "repetida", id, Texto(mensagem, "type"), Mascarar(Texto(mensagem, "from")),
                        phoneNumberId, display, payload.Length);
                    if (novo) novos++; else repetidos++;
                }
            }

            if (valor.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            {
                foreach (var status in statuses.EnumerateArray())
                {
                    var id = Texto(status, "id");
                    var situacao = Texto(status, "status");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(situacao)) continue;

                    var payload = JsonSerializer.Serialize(new { metadata = Clonar(metadata), status = Clonar(status) });
                    var novo = await _eventos.RegistrarAsync(new WaEvento
                    {
                        Id = Guid.NewGuid(), Tipo = TipoEvento.Status, Chave = $"{id}:{situacao}",
                        PhoneNumberId = phoneNumberId, DisplayPhone = display, PayloadJson = payload
                    });

                    _logger.LogInformation(
                        "[wa.in] ev={Evento} wa={Wa} status={Status} phone_number_id={PhoneNumberId}",
                        novo ? "status_recebido" : "status_repetido", id, situacao, phoneNumberId);
                    if (novo) novos++; else repetidos++;
                }
            }

            return (novos, repetidos);
        }

        private static string? Texto(JsonElement elemento, string propriedade) =>
            elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(propriedade, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        // JsonElement e uma "visao" do documento; clonar permite serializar depois que o documento for descartado.
        private static object? Clonar(JsonElement elemento) =>
            elemento.ValueKind == JsonValueKind.Undefined ? null : elemento.Clone();

        private static string Mascarar(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return "(vazio)";
            return valor.Length <= 4 ? valor : new string('*', valor.Length - 4) + valor[^4..];
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
