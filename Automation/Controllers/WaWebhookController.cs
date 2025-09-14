// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Controllers
{
    [ApiController]
    [Route("wa")]
    public class WaWebhookController : ControllerBase
    {
        private readonly ILogger<WaWebhookController> _logger;
        private readonly IWebhookSignatureValidator _validadorAssinatura;
        private readonly ConversationService _servicoConversa;
        private readonly IQueueBus _fila;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            IWebhookSignatureValidator validadorAssinatura,
            ConversationService servicoConversa,
            IQueueBus fila)
        {
            _logger = logger;
            _validadorAssinatura = validadorAssinatura;
            _servicoConversa = servicoConversa;
            _fila = fila;
        }

        [HttpGet("webhook")]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string mode,
            [FromQuery(Name = "hub.verify_token")] string token,
            [FromQuery(Name = "hub.challenge")] string challenge)
        {
            const string VERIFY_TOKEN = "zippygo123"; // mesmo que voc� colocar no painel do Meta

            if (mode == "subscribe" && token == VERIFY_TOKEN)
            {
                _logger.LogInformation("Webhook verificado com sucesso pelo Meta.");
                return Ok(challenge); // devolve o challenge
            }
            else
            {
                _logger.LogWarning("Falha na verificação do webhook. Token inválido.");
                return Forbid(); // 403
            }
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook()
        {
            try
            {
                // Signature validation (when enabled)
                Request.EnableBuffering();
                string corpo;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    corpo = await reader.ReadToEndAsync();
                    Request.Body.Position = 0;
                }

                _logger.LogInformation("Webhook recebido: {Payload}", corpo);

                var cabecalho = Request.Headers["X-Hub-Signature-256"].ToString();
                if (!_validadorAssinatura.ValidarXHubSignature256(cabecalho, corpo))
                {
                    _logger.LogWarning("Invalid X-Hub-Signature-256");
                    return Ok(); // Retorna 200 mesmo com assinatura inválida
                }

                WebhookPayloadDto? carga = null;
                try
                {
                    carga = JsonSerializer.Deserialize<WebhookPayloadDto>(corpo);
                }
                catch
                {
                    // invalid payload: ack 200 to avoid excessive retries
                    return Ok();
                }

                // Processar todas as entradas e mudanças
                if (carga?.Entradas != null)
                {
                    foreach (var entrada in carga.Entradas)
                    {
                        if (entrada.Mudancas != null)
                        {
                            foreach (var mudanca in entrada.Mudancas)
                            {
                                if (mudanca.Campo == "messages" && mudanca.Valor?.Mensagens != null)
                                {
                                    // Extrair phone_number_id dos metadados
                                    var phoneNumberId = mudanca.Valor.Metadados?.IdNumeroTelefone;
                                    
                                    foreach (var mensagem in mudanca.Valor.Mensagens)
                                    {
                                        try
                                        {
                                            if (mensagem.De != null && !string.IsNullOrWhiteSpace(mensagem.Id))
                                            {
                                                var texto = mensagem.Texto?.Corpo ?? string.Empty;
                                                var criada = await _servicoConversa.AcrescentarEntradaAsync(mensagem.De!, mensagem.Id!, texto);

                                                // Lógica de detecção de handover
                                                if (criada != null && DetectaHandover(texto))
                                                {
                                                    await ChamarHandoverEndpointAsync(criada.IdConversa, "AgenteX");
                                                }

                                                if (criada != null)
                                                {
                                                    await _fila.PublicarEntradaAsync(criada);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Erro ao processar mensagem individual {MensagemId} de {De}", mensagem.Id, mensagem.De);
                                            // Continua processando outras mensagens
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro crítico ao processar webhook");
                return Ok(); // SEMPRE retorna 200 para evitar reenvios do WhatsApp
            }
        }

        // Fun��o para detectar inten��o de handover
        private bool DetectaHandover(string texto)
        {
            return texto.Contains("atendente", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("humano", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("pessoa", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("gente", StringComparison.OrdinalIgnoreCase)
                || texto.Contains("falar com algu�m", StringComparison.OrdinalIgnoreCase);
        }

        // Fun��o para chamar o endpoint de handover
        private async Task ChamarHandoverEndpointAsync(Guid idConversa, string agenteDesignado)
        {
            using var httpClient = new HttpClient();
            var url = $"https://seuservidor/api/automation/conversation/{idConversa}/handover";
            var body = JsonSerializer.Serialize(new { AgenteDesignado = agenteDesignado });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            await httpClient.PostAsync(url, content);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
