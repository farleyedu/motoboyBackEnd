// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Services
{
    // Envia alertas reais via Telegram Bot API usando configurações em Automation:Telegram
    public class AlertSenderTelegram : IAlertSender
    {
        private const int TelegramMaxLength = 4096;

        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptions<AutomationOptions> _options;
        private readonly ILogger<AlertSenderTelegram> _logger;

        public AlertSenderTelegram(
            IHttpClientFactory httpFactory,
            IOptions<AutomationOptions> options,
            ILogger<AlertSenderTelegram> logger)
        {
            _httpFactory = httpFactory;
            _options = options;
            _logger = logger;
        }

        public async Task EnviarAlertaAsync(string mensagem, string? chatIdOverride = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mensagem)) return;

                var cfg = _options.Value?.Telegram;
                var token = cfg?.BotToken;
                var chatId = string.IsNullOrWhiteSpace(chatIdOverride) ? cfg?.ChatId : chatIdOverride;

                if (string.IsNullOrWhiteSpace(token) || token == "<TODO>" || string.IsNullOrWhiteSpace(chatId) || chatId == "<TODO>")
                {
                    _logger.LogWarning("Telegram BotToken/ChatId não configurados. Alerta não enviado.");
                    return;
                }

                var endpoint = $"https://api.telegram.org/bot{token}/sendMessage";
                var client = _httpFactory.CreateClient();

                // Envia em partes caso exceda limite do Telegram
                int idx = 0;
                while (idx < mensagem.Length)
                {
                    var restante = mensagem.Length - idx;
                    var take = Math.Min(TelegramMaxLength, restante);
                    var trecho = mensagem.Substring(idx, take);
                    idx += take;

                    var payload = new
                    {
                        chat_id = chatId,
                        text = trecho,
                        disable_web_page_preview = true,
                        allow_sending_without_reply = true
                    };

                    var json = JsonSerializer.Serialize(payload);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync(endpoint, content);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        _logger.LogWarning("Falha ao enviar alerta Telegram: {Status} {Body}", (int)resp.StatusCode, body);
                        // Não interrompe loop para partes seguintes
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao enviar alerta para Telegram");
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
