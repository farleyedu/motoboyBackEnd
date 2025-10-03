// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
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

        public Task SendTextAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                return Task.CompletedTask;
            }

            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "text",
                text = new { body = texto }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "text");
        }

        public Task SendImageAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string imageUrl)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "image",
                image = new { link = imageUrl }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "image");
        }

        public Task SendDocumentAsync(Guid idConversa, string phoneNumberId, string numeroDestino, string documentUrl, string? filename)
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to = TelefoneHelper.NormalizeBrazilianForWhatsappTo(numeroDestino),
                type = "document",
                document = new { link = documentUrl, filename = string.IsNullOrWhiteSpace(filename) ? null : filename }
            };

            return SendPayloadAsync(idConversa, phoneNumberId, payload, "document");
        }

        private async Task SendPayloadAsync(Guid idConversa, string phoneNumberId, object payload, string payloadType)
        {
            var token = _tokenProvider.GetAccessToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("[Conversa={Conversa}] Token do WhatsApp nao configurado", idConversa);
                return;
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var graphVersion = _configuration["WhatsApp:GraphApiVersion"] ?? "v23.0";
            var endpoint = $"https://graph.facebook.com/{graphVersion}/{phoneNumberId}/messages";
            var json = JsonSerializer.Serialize(payload);
            _logger.LogDebug("[Conversa={Conversa}] Payload WhatsApp ({Tipo}): {Json}", idConversa, payloadType, json);

            var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

            for (var tentativa = 0; tentativa < delays.Length; tentativa++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    var resposta = await client.PostAsync(endpoint, content);
                    var body = await resposta.Content.ReadAsStringAsync();

                    if (resposta.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("[Conversa={Conversa}] Mensagem ({Tipo}) enviada via WhatsApp", idConversa, payloadType);
                        return;
                    }

                    _logger.LogWarning(
                        "[Conversa={Conversa}] Falha ao enviar WhatsApp ({Tipo}, tentativa {Tentativa}): {Status} - {Body}",
                        idConversa,
                        payloadType,
                        tentativa + 1,
                        (int)resposta.StatusCode,
                        body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Conversa={Conversa}] Erro ao enviar WhatsApp ({Tipo}, tentativa {Tentativa})", idConversa, payloadType, tentativa + 1);
                }

                if (tentativa < delays.Length - 1)
                {
                    await Task.Delay(delays[tentativa]);
                }
            }

            _logger.LogError("[Conversa={Conversa}] Todas as tentativas de envio ({Tipo}) falharam", idConversa, payloadType);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
