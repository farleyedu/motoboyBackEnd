// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra.Config;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Services
{
    public class OpenAIAssistantService : IAssistantService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        public Task<AssistantDecision> GerarDecisaoAsync(string textoUsuario, Guid idConversa, object? contexto = null)
            => GerarDecisaoInternoAsync(textoUsuario, idConversa, contexto, historico: null);

        public Task<AssistantDecision> GerarDecisaoComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null)
            => GerarDecisaoInternoAsync(textoUsuario, idConversa, contexto, historico);

        private async Task<AssistantDecision> GerarDecisaoInternoAsync(string textoUsuario, Guid idConversa, object? contexto, IEnumerable<AssistantChatTurn>? historico)
        {
            var apiKey = _options.Value.ApiKey;
            var model = string.IsNullOrWhiteSpace(_options.Value.Model) ? "gpt-4o-mini" : _options.Value.Model;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Conversa={Conversa}] OpenAI ApiKey não configurada; usando decisão padrão", idConversa);
                return new AssistantDecision(
                    Reply: string.IsNullOrWhiteSpace(textoUsuario) ? "Poderia repetir?" : $"Você disse: '{textoUsuario}'.",
                    HandoverAction: "none",
                    AgentPrompt: null,
                    ReservaConfirmada: false,
                    Detalhes: null);
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var systemPrompt = contexto as string ?? "Você é um assistente útil.";
            var messages = new List<object> { new { role = "system", content = systemPrompt } };

            if (historico != null)
            {
                foreach (var turn in historico)
                {
                    if (string.IsNullOrWhiteSpace(turn.Content)) continue;
                    var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                    messages.Add(new { role, content = turn.Content });
                }
            }

            messages.Add(new { role = "user", content = textoUsuario });

            var payload = new { model, messages = messages.ToArray() };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI falhou: {Status} {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision("Desculpe, não consegui formular uma resposta agora.", "none", null, false, null);
                }

                using var doc = JsonDocument.Parse(body);
                var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return InterpretarResposta(message, idConversa);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro ao chamar OpenAI", idConversa);
                return new AssistantDecision("Desculpe, ocorreu um erro ao gerar a resposta.", "none", null, false, null);
            }
        }

        private static AssistantDecision InterpretarResposta(string? conteudo, Guid idConversa)
        {
            if (string.IsNullOrWhiteSpace(conteudo))
            {
                return new AssistantDecision(string.Empty, "none", null, false, null);
            }

            try
            {
                var dto = JsonSerializer.Deserialize<AssistantDecisionDto>(conteudo, JsonOptions);
                if (dto != null)
                {
                    var reply = dto.reply ?? string.Empty;
                    var action = string.IsNullOrWhiteSpace(dto.handover) ? "none" : dto.handover!.Trim().ToLowerInvariant();
                    var agentPrompt = string.IsNullOrWhiteSpace(dto.agent_prompt) ? null : dto.agent_prompt!.Trim();
                    var confirmada = dto.reserva_confirmada ?? false;
                    return new AssistantDecision(reply, action, agentPrompt, confirmada, dto.detalhes);
                }
            }
            catch
            {
                // Conteúdo não era JSON estruturado; segue fallback textual
            }

            return new AssistantDecision(conteudo, "none", null, false, null);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
