// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Infra.Config;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Services
{
    public class OpenAIAssistantService : IAssistantService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptions<OpenAIOptions> _options;
        private readonly ILogger<OpenAIAssistantService> _logger;

        public OpenAIAssistantService(
            IHttpClientFactory httpFactory,
            IOptions<OpenAIOptions> options,
            ILogger<OpenAIAssistantService> logger)
        {
            _httpFactory = httpFactory;
            _options = options;
            _logger = logger;
        }

        public async Task<string> GerarRespostaAsync(string textoUsuario, Guid idConversa, object? contexto = null)
        {
            var apiKey = _options.Value.ApiKey;
            var model = string.IsNullOrWhiteSpace(_options.Value.Model) ? "gpt-4o-mini" : _options.Value.Model;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("OpenAI ApiKey nula; retornando resposta padrão");
                return string.IsNullOrWhiteSpace(textoUsuario) ? "Poderia repetir?" : $"Você disse: '{textoUsuario}'.";
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var sys = contexto as string ?? "Você é um assistente útil.";
            var payload = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user", content = textoUsuario }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenAI falhou: {Status} {Body}", (int)resp.StatusCode, body);
                    return "Desculpe, não consegui formular uma resposta agora.";
                }

                using var doc = JsonDocument.Parse(body);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0)
                {
                    return string.Empty;
                }
                var msg = choices[0].GetProperty("message").GetProperty("content").GetString();
                return msg ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao chamar OpenAI para conversa {Conversa}", idConversa);
                return "Desculpe, ocorreu um erro ao gerar a resposta.";
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

