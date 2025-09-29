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

        public async Task EnviarAlertaTelegramAsync(string mensagem, string? chatIdOverride = null)
        {
            if (string.IsNullOrWhiteSpace(mensagem)) return;

            var conversationId = ExtrairIdentificadorConversa(mensagem);
            var cfg = _options.Value?.Telegram;
            var token = cfg?.BotToken;
            var chatId = string.IsNullOrWhiteSpace(chatIdOverride) ? cfg?.ChatId : chatIdOverride;

            if (string.IsNullOrWhiteSpace(token) || token == "<TODO>" || string.IsNullOrWhiteSpace(chatId) || chatId == "<TODO>")
            {
                _logger.LogWarning("[Conversa={Conversa}] Telegram BotToken/ChatId não configurados. Alerta não enviado.", conversationId);
                return;
            }

            var endpoint = $"https://api.telegram.org/bot{token}/sendMessage";
            var client = _httpFactory.CreateClient();
            var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

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

                var enviado = false;
                for (var tentativa = 0; tentativa < delays.Length; tentativa++)
                {
                    try
                    {
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        var resp = await client.PostAsync(endpoint, content);
                        if (resp.IsSuccessStatusCode)
                        {
                            enviado = true;
                            break;
                        }

                        var body = await resp.Content.ReadAsStringAsync();
                        _logger.LogWarning("[Conversa={Conversa}] Falha ao enviar alerta Telegram (tentativa {Tentativa}): {Status} {Body}", conversationId, tentativa + 1, (int)resp.StatusCode, body);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Conversa={Conversa}] Erro ao enviar alerta Telegram (tentativa {Tentativa})", conversationId, tentativa + 1);
                    }

                    await Task.Delay(delays[tentativa]);
                }

                if (!enviado)
                {
                    _logger.LogError("[Conversa={Conversa}] Falha definitiva ao enviar alerta Telegram", conversationId);
                    break;
                }
            }
        }

        private static Guid? ExtrairIdentificadorConversa(string mensagem)
        {
            const string marcador = "Conversa=";
            var idx = mensagem.LastIndexOf(marcador, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var inicio = idx + marcador.Length;
            if (inicio >= mensagem.Length) return null;

            var trecho = mensagem.Substring(inicio).Trim();
            var fim = trecho.IndexOfAny(new[] { '\r', '\n', ' ' });
            if (fim >= 0)
            {
                trecho = trecho.Substring(0, fim);
            }

            return Guid.TryParse(trecho, out var guid) ? guid : (Guid?)null;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


