// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using APIBack.Automation.Helpers;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class WhatsAppSender
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IWhatsAppTokenProvider _tokenProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WhatsAppSender> _logger;

        public WhatsAppSender(
            IHttpClientFactory httpFactory,
            IWhatsAppTokenProvider tokenProvider,
            IConfiguration configuration,
            ILogger<WhatsAppSender> logger)
        {
            _httpFactory = httpFactory;
            _tokenProvider = tokenProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendTextAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return;

            var token = _tokenProvider.GetAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("[Conversa={Conversa}] Token do WhatsApp não configurado", idConversa);
                return;
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var graphVersion = _configuration["WhatsApp:GraphApiVersion"] ?? "v17.0";
            var endpoint = $"https://graph.facebook.com/{graphVersion}/{phoneNumberId}/messages";
            var numeroDestinoNormalizado = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino);
            var payload = new
            {
                messaging_product = "whatsapp",
                to = numeroDestinoNormalizado,
                type = "text",
                text = new { body = texto }
            };

            var json = JsonSerializer.Serialize(payload);
            var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
            for (var tentativa = 0; tentativa < delays.Length; tentativa++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                try
                {
                    var resposta = await client.PostAsync(endpoint, content);
                    if (resposta.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("[Conversa={Conversa}] Mensagem enviada via WhatsApp", idConversa);
                        return;
                    }

                    var body = await resposta.Content.ReadAsStringAsync();
                    _logger.LogWarning("[Conversa={Conversa}] Falha ao enviar WhatsApp (tentativa {Tentativa}): {Status} {Body}", idConversa, tentativa + 1, (int)resposta.StatusCode, body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Conversa={Conversa}] Erro ao enviar WhatsApp (tentativa {Tentativa})", idConversa, tentativa + 1);
                }

                await Task.Delay(delays[tentativa]);
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


