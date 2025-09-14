// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Automation.Infra;
using Microsoft.Extensions.Options;
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
        private readonly IAssistantService? _ia;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptions<AutomationOptions> _opcoes;
        private readonly IWabaPhoneRepository _wabaRepo;
        private readonly IIARegraRepository _regrasRepo;
        private readonly IIARespostaRepository _respostasRepo;

        public WaWebhookController(
            ILogger<WaWebhookController> logger,
            IWebhookSignatureValidator validadorAssinatura,
            ConversationService servicoConversa,
            IQueueBus fila,
            IHttpClientFactory httpFactory,
            IOptions<AutomationOptions> opcoes,
            IWabaPhoneRepository wabaRepo,
            IIARegraRepository regrasRepo,
            IIARespostaRepository respostasRepo,
            IAssistantService? ia = null)
        {
            _logger = logger;
            _validadorAssinatura = validadorAssinatura;
            _servicoConversa = servicoConversa;
            _fila = fila;
            _httpFactory = httpFactory;
            _opcoes = opcoes;
            _ia = ia;
            _wabaRepo = wabaRepo;
            _regrasRepo = regrasRepo;
            _respostasRepo = respostasRepo;
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
                                    var phoneNumberEstabelecimento = mudanca.Valor.Metadados?.NumeroTelefoneExibicao;

                                    foreach (var mensagem in mudanca.Valor.Mensagens)
                                    {
                                        try
                                        {
                                            if (mensagem.De != null && !string.IsNullOrWhiteSpace(mensagem.Id))
                                            {
                                                var texto = mensagem.Texto?.Corpo ?? string.Empty;
                                                DateTime? dataMsgUtc = null;
                                                if (!string.IsNullOrWhiteSpace(mensagem.CarimboTempo) && long.TryParse(mensagem.CarimboTempo, out var unix))
                                                {
                                                    try { dataMsgUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch { /* ignora parse inválido */ }
                                                }
                                                // Use o display number (numero exibido) como parâmetro para o service,
                                                // mantendo o phoneNumberId (WABA) intacto para usos posteriores
                                                var criada = await _servicoConversa.AcrescentarEntradaAsync(mensagem.De!, mensagem.Id!, texto, phoneNumberEstabelecimento,  dataMsgUtc );

                                                // Lógica de detecção de handover
                                                if (criada != null && DetectaHandover(texto))
                                                {
                                                    await ChamarHandoverEndpointAsync(criada.IdConversa, "AgenteX");
                                                }

                                                if (criada != null)
                                                {
                                                    await _fila.PublicarEntradaAsync(criada);
                                                    try
                                                    {
                                                        Guid? idEstab = null;
                                                        if (!string.IsNullOrWhiteSpace(phoneNumberEstabelecimento))
                                                            idEstab = await _wabaRepo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberEstabelecimento);

                                                        Guid? idRegraAplicada = null;
                                                        string? contexto = null;
                                                        if (idEstab.HasValue && idEstab.Value != Guid.Empty)
                                                        {
                                                            var regras = await _regrasRepo.ListaregrasAsync(idEstab.Value);
                                                            var ativa = regras?.Where(r => r.Ativo)
                                                                               .OrderByDescending(r => r.DataAtualizacao)
                                                                               .ThenByDescending(r => r.DataCriacao)
                                                                               .FirstOrDefault();
                                                            if (ativa != null)
                                                            {
                                                                idRegraAplicada = ativa.Id;
                                                                contexto = ativa.Contexto;
                                                            }
                                                            else
                                                            {
                                                                contexto = await _regrasRepo.ObterContextoAtivoAsync(idEstab.Value);
                                                            }
                                                        }

                                                        var respostaIa = _ia != null ? await _ia.GerarRespostaAsync(texto, criada.IdConversa, contexto) : null;
                                                        if (!string.IsNullOrWhiteSpace(respostaIa) && !string.IsNullOrWhiteSpace(phoneNumberEstabelecimento) && !string.IsNullOrWhiteSpace(mensagem.De))
                                                        {
                                                            await EnviarRespostaWhatsAppAsync(phoneNumberEstabelecimento!, mensagem.De!, respostaIa!);
                                                            _logger.LogInformation("Resposta automática enviada para {Destino}", mensagem.De);
                                                        }
                                                    }
                                                    catch (Exception exAuto)
                                                    {
                                                        _logger.LogError(exAuto, "Falha ao gerar/enviar resposta automática");
                                                    }
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

        // Envia resposta de texto via WhatsApp Cloud API (Graph)
        private async Task EnviarRespostaWhatsAppAsync(string phoneNumberId, string numeroDestino, string textoResposta)
        {
            try
            {
                var token = _opcoes.Value.Meta?.AccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogWarning("Token do WhatsApp (Meta.AccessToken) não configurado");
                    return;
                }

                var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var endpoint = $"https://graph.facebook.com/v17.0/{phoneNumberId}/messages";
                var payload = new
                {
                    messaging_product = "whatsapp",
                    to = numeroDestino,
                    type = "text",
                    text = new { body = textoResposta }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(endpoint, content);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("Falha ao enviar resposta WA: {Status} {Body}", (int)resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar resposta para WhatsApp");
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================



