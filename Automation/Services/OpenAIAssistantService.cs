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
        private readonly IMessageRepository _messageRepository;
        private readonly ToolExecutorService _toolExecutor;

        public OpenAIAssistantService(
            IHttpClientFactory httpFactory,
            IOptions<OpenAIOptions> options,
            ILogger<OpenAIAssistantService> logger,
            IMessageRepository messageRepository,
            ToolExecutorService toolExecutor)
        {
            _httpFactory = httpFactory;
            _options = options;
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
            var apiKey = _options.Value.ApiKey;
            var model = string.IsNullOrWhiteSpace(_options.Value.Model) ? "gpt-4o-2024-08-06" : _options.Value.Model;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Conversa={Conversa}] OpenAI ApiKey não configurada; usando decisão padrão", idConversa);
                return new AssistantDecision(
                    Reply: "Desculpe, não consegui gerar uma resposta agora.",
                    HandoverAction: "none",
                    AgentPrompt: null,
                    ReservaConfirmada: false,
                    Detalhes: null
                );
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // 🔹 Prompt completo do Seu Eurico (identidade, horários, cardápio, regras de reserva e escalonamento)
            var systemPrompt = contexto as string ?? @"
Você é um agente virtual acolhedor que atende clientes do Bar Seu Eurico 🍻✨.
Sua missão: responder dúvidas (horário, endereço, cardápio) e organizar reservas com carinho 🌸.
Use sempre JSON com a estrutura:
{
  ""reply"": ""string"",
  ""agentPrompt"": ""string|null"",
  ""nomeCompleto"": ""string|null"",
  ""qtdPessoas"": ""int|null"",
  ""data"": ""string|null"",
  ""hora"": ""string|null""
}
Regras:
- Antes de confirmar reserva ou escalar humano, SEMPRE peça confirmação do cliente.
- Só confirme reserva se tiver nome completo, quantidade, data e hora.
- Respeite horário de funcionamento: Seg-Sex 17h–00h30, Sáb 12h–01h, Dom 12h–00h30.
- Promoções e cardápio devem ser respondidos com tom simpático e emojis.
- Escalação para humano segue fluxo de 2 passos (pergunta → confirmação → tool).
";

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

            var tools = _toolExecutor.GetDeclaredTools(idConversa);
            _logger.LogInformation("[Conversa={Conversa}] Enviando {Count} tools para OpenAI", idConversa, tools.Length);

            var payload = new
            {
                model,
                input = messages.ToArray(),
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "assistant_decision",
                        schema = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                reply = new { type = "string", minLength = 1 },
                                agentPrompt = new { type = new[] { "string", "null" } },
                                nomeCompleto = new { type = new[] { "string", "null" } },
                                qtdPessoas = new { type = new[] { "integer", "null" } },
                                data = new { type = new[] { "string", "null" } },
                                hora = new { type = new[] { "string", "null" } }
                            },
                            required = new[] { "reply", "agentPrompt", "nomeCompleto", "qtdPessoas", "data", "hora" }
                        }
                    }
                },
                tools
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogInformation("[Conversa={Conversa}] Payload enviado para OpenAI: {Payload}", idConversa, json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync("https://api.openai.com/v1/responses", content);
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogInformation("[Conversa={Conversa}] Resposta bruta da OpenAI: {Body}", idConversa, body);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI falhou: {Status} {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision("Desculpe, não consegui formular uma resposta agora.", "none", null, false, null);
                }

                using var doc = JsonDocument.Parse(body);
                var outputArray = doc.RootElement.GetProperty("output");

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

                        var result = await _toolExecutor.ExecuteToolAsync(toolName!, args);

                        // 🔹 Sempre devolver JSON padronizado
                        return new AssistantDecision(
                            Reply: result,
                            HandoverAction: toolName,
                            AgentPrompt: null,
                            ReservaConfirmada: toolName == "confirmar_reserva",
                            Detalhes: null
                        );
                    }
                }

                // 🔹 Fallback padronizado
                return new AssistantDecision(
                    Reply: "Desculpe, não entendi sua solicitação. Pode reformular, por favor? 😊",
                    HandoverAction: "none",
                    AgentPrompt: null,
                    ReservaConfirmada: false,
                    Detalhes: null
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro ao chamar OpenAI", idConversa);
                return new AssistantDecision(
                    Reply: "Desculpe, ocorreu um erro ao gerar a resposta.",
                    HandoverAction: "none",
                    AgentPrompt: null,
                    ReservaConfirmada: false,
                    Detalhes: null
                );
            }
        }


        private async Task<AssistantDecision> InterpretarResposta(string? conteudo, Guid idConversa)
        {
            var parseResult = await AssistantDecisionParser.TryParse(conteudo, JsonOptions, _logger, idConversa, _messageRepository);

            if (parseResult.Success) return parseResult.Decision;

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
            if (string.IsNullOrEmpty(texto)) return string.Empty;
            return texto.Length <= maxLength ? texto : texto.Substring(0, maxLength) + "...";
        }
    }
}