// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class AssistantService : IAssistantService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<AssistantService> _logger;
        private readonly IMessageRepository _messageRepository;
        private readonly ToolExecutorService _toolExecutor;

        public AssistantService(
            IHttpClientFactory httpFactory,
            ILogger<AssistantService> logger,
            IMessageRepository messageRepository,
            ToolExecutorService toolExecutor)
        {
            _httpFactory = httpFactory;
            _logger = logger;
            _messageRepository = messageRepository;
            _toolExecutor = toolExecutor;
        }

        public Task<AssistantDecision> GerarDecisaoAsync(string textoUsuario, Guid idConversa, object? contexto = null)
            => GerarDecisaoInternoAsync(textoUsuario, idConversa, contexto, historico: null);

        public Task<AssistantDecision> GerarDecisaoComHistoricoAsync(Guid idConversa, string textoUsuario, IEnumerable<AssistantChatTurn> historico, object? contexto = null)
            => GerarDecisaoInternoAsync(textoUsuario, idConversa, contexto, historico);

        private async Task<AssistantDecision> GerarDecisaoInternoAsync(string textoUsuario, Guid idConversa, object? contexto, IEnumerable<AssistantChatTurn>? historico)
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Conversa={Conversa}] OPENAI_API_KEY não configurada; usando decisão padrão", idConversa);
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

            // Simplificação: A API "Responses" não usa response_format para JSON, ela infere pelo prompt.
            // Para forçar JSON, o melhor é instruir no prompt de sistema e ter um bom parser.
            // O uso de `text.format` com `json_schema` foi o que causou os erros anteriores.
            var payload = new
            {
                model,
                input = messages.ToArray(),
                tools = _toolExecutor.GetDeclaredTools(idConversa)
                // O parâmetro 'text' foi removido para simplificar e evitar erros de schema.
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogInformation("[Conversa={Conversa}] Payload enviado para OpenAI: {Payload}", idConversa, json);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync("https://api.openai.com/v1/responses", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI falhou: {Status} {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision("Desculpe, não consegui formular uma resposta agora.", "none", null, false, null);
                }

                _logger.LogInformation("[Conversa={Conversa}] Resposta bruta da OpenAI: {Body}", idConversa, body);

                using var doc = JsonDocument.Parse(body);
                var outputArray = doc.RootElement.GetProperty("output");

                string? toolResult = null;

                foreach (var item in outputArray.EnumerateArray())
                {
                    var type = item.GetProperty("type").GetString();

                    if (type == "message")
                    {
                        var message = item
                            .GetProperty("content")[0]
                            .GetProperty("text")
                            .GetString();

                        return await InterpretarResposta(message, idConversa);
                    }
                    else if (type == "tool_call" || type == "function_call")
                    {
                        var toolName = item.GetProperty("name").GetString();
                        var args = item.GetProperty("arguments").GetRawText();

                        // Executa a tool, mas guarda o resultado.
                        // A IA pode chamar múltiplas tools, vamos processar todas.
                        toolResult = await _toolExecutor.ExecuteToolAsync(toolName!, args);
                    }
                }

                // Se houve resultado de uma tool, retornamos ele como a decisão final.
                if (toolResult != null)
                {
                    return new AssistantDecision(toolResult, "none", null, false, null);
                }

                return new AssistantDecision("Desculpe, não entendi a solicitação.", "none", null, false, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro ao chamar OpenAI", idConversa);
                return new AssistantDecision("Desculpe, ocorreu um erro ao gerar a resposta.", "none", null, false, null);
            }
        }

        private async Task<AssistantDecision> InterpretarResposta(string? conteudo, Guid idConversa)
        {
            var parseResult = await AssistantDecisionParser.TryParse(conteudo, JsonOptions, _logger, idConversa, _messageRepository);

            if (parseResult.Success)
            {
                return parseResult.Decision;
            }

            if (!string.IsNullOrWhiteSpace(parseResult.ExtractedJson))
            {
                _logger.LogWarning("[Conversa={Conversa}] JSON retornado pela IA não pôde ser interpretado: {Json}", idConversa, parseResult.ExtractedJson);
            }
            else if (!string.IsNullOrWhiteSpace(conteudo))
            {
                _logger.LogWarning("[Conversa={Conversa}] Resposta da IA fora do formato JSON esperado. Prévia: {Preview}", idConversa, TruncarConteudo(conteudo));
            }

            return new AssistantDecision(conteudo ?? string.Empty, "none", null, false, null);
        }

        private static string TruncarConteudo(string? texto, int maxLength = 300)
        {
            if (string.IsNullOrEmpty(texto))
            {
                return string.Empty;
            }
            return texto.Length <= maxLength ? texto : texto.Substring(0, maxLength) + "...";
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================
