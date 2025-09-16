// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Services
{
    public class AssistantService : IAssistantService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<AssistantService> _logger;

        public AssistantService(IHttpClientFactory httpFactory, ILogger<AssistantService> logger)
        {
            _httpFactory = httpFactory;
            _logger = logger;
        }

        public async Task<string> GerarRespostaAsync(string textoUsuario, Guid idConversa, object? contexto = null)
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-3.5-turbo";

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("OPENAI_API_KEY não configurada; retornando resposta stub");
                return string.IsNullOrWhiteSpace(textoUsuario)
                    ? "Poderia repetir?"
                    : $"Você disse: '{textoUsuario}'.";
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
                var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return msg ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao chamar OpenAI para conversa {Conversa}", idConversa);
                return "Desculpe, ocorreu um erro ao gerar a resposta.";
            }
        }

        public Task<string> GerarRespostaComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null)
        {
            // Implementação simples: reutiliza o método existente, ignorando o histórico neste stub.
            return GerarRespostaAsync(textoUsuario, idConversa, contexto);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
