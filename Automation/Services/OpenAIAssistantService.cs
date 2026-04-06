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

            // ✨ SANITIZAÇÃO ROBUSTA DA API KEY
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var apiKeyOriginalLength = apiKey.Length;

                // Remove TODOS os caracteres de espaço em branco (espaços, tabs, \n, \r, etc)
                apiKey = new string(apiKey.Where(c => !char.IsWhiteSpace(c)).ToArray());

                _logger.LogDebug(
                    "[Conversa={Conversa}] [APIKEY-DEBUG] Tamanho original={TamanhoOriginal}, Tamanho limpo={TamanhoLimpo}",
                    idConversa,
                    apiKeyOriginalLength,
                    apiKey.Length);

                // Validação básica de formato OpenAI (sk-proj-... ou sk-...)
                if (!apiKey.StartsWith("sk-"))
                {
                    _logger.LogError(
                        "[Conversa={Conversa}] [APIKEY-ERROR] API Key não começa com 'sk-'",
                        idConversa);

                    return new AssistantDecision(
                        Reply: "Erro de configuração: API Key inválida. Contate o suporte.",
                        HandoverAction: "none",
                        AgentPrompt: null,
                        ReservaConfirmada: false,
                        Detalhes: null);
                }

                // Validação de tamanho mínimo (chaves OpenAI têm ~90+ caracteres)
                if (apiKey.Length < 40)
                {
                    _logger.LogError(
                        "[Conversa={Conversa}] [APIKEY-ERROR] API Key muito curta (tamanho={Tamanho})",
                        idConversa,
                        apiKey.Length);

                    return new AssistantDecision(
                        Reply: "Erro de configuração: API Key incompleta. Contate o suporte.",
                        HandoverAction: "none",
                        AgentPrompt: null,
                        ReservaConfirmada: false,
                        Detalhes: null);
                }
            }

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

⚠️ **REGRAS CRÍTICAS SOBRE CONFIRMAÇÃO/NEGAÇÃO:**

1. **SEMPRE interprete corretamente negações:**
   - ""não está correto"" = cliente está RECUSANDO/CORRIGINDO (NÃO confirme!)
   - ""não"" = cliente está NEGANDO (NÃO confirme!)
   - ""está errado"" = cliente está CORRIGINDO (NÃO confirme!)
   - ""não é isso"" = cliente está NEGANDO (NÃO confirme!)

2. **Quando cliente disser algo negativo:**
   - NUNCA confirme a ação
   - SEMPRE pergunte: ""O que está incorreto? Vamos corrigir! 😊""
   - Aguarde cliente especificar o que precisa mudar
   - Seja específico: pergunte ""O que você quer alterar: a data, o horário ou a quantidade de pessoas?""

3. **Confirmação só acontece com frases AFIRMATIVAS:**
   - ""sim"", ""confirmar"", ""está certo"", ""pode confirmar"", ""isso mesmo""
   - ""ok"", ""tudo certo"", ""perfeito"", ""correto""
   - Qualquer frase CLARAMENTE afirmativa

4. **Na dúvida, sempre pergunte novamente ao invés de assumir:**
   - Melhor pedir confirmação 2x do que confirmar errado!

**EXEMPLOS DO QUE NÃO FAZER:**
❌ Cliente: ""não está correto"" → Bot confirma (ERRADO!)
❌ Cliente: ""não"" → Bot confirma (ERRADO!)
❌ Cliente: ""está errado"" → Bot confirma (ERRADO!)

**EXEMPLOS DO QUE FAZER:**
✅ Cliente: ""não está correto"" → Bot: ""O que está incorreto? Vamos corrigir! Você quer mudar a data, o horário ou a quantidade de pessoas?""
✅ Cliente: ""não"" → Bot: ""Entendi! O que você gostaria de alterar?""
✅ Cliente: ""está errado"" → Bot: ""Desculpa pela confusão! Me diz o que está errado que eu corrijo rapidinho 😊""

**REGRA DE OURO:** Se a resposta do cliente contém ""não"", ""errado"", ""incorreto"", ""não é isso"" → NÃO CONFIRME NADA!

Regras gerais:
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

            var tools = await _toolExecutor.GetDeclaredToolsAsync(idConversa);
            _logger.LogInformation("[Conversa={Conversa}] Enviando {Count} tools para OpenAI", idConversa, tools.Length);

            // ===== BUG 2 FIX: Garantir ResponseFormat estruturado =====
            var payload = new
            {
                model,
                messages = messages.ToArray(),  // ⬅️ CORREÇÃO: Era "input", deve ser "messages"
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "assistant_response",
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                reply = new
                                {
                                    type = "string",
                                    description = "Resposta principal para o usuario"
                                },
                                handover_action = new
                                {
                                    type = "string",
                                    @enum = new[] { "none", "human", "agent" },
                                    description = "Acao de handover: none, human ou agent"
                                }
                            },
                            required = new[] { "reply", "handover_action" },
                            additionalProperties = false
                        },
                        strict = true  // ⬅️ CRÍTICO: Força o schema rigoroso
                    }
                },
                tools
            };

            _logger.LogDebug(
                "[Conversa={Conversa}] ResponseFormat configurado com json_schema strict=true",
                idConversa);
            // ===== FIM BUG 2 FIX =====

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var payloadPreview = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
            _logger.LogTrace(
                "[Conversa={Conversa}] Payload enviado para OpenAI (len={Length}): {Preview}",
                idConversa,
                json.Length,
                payloadPreview);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                // ===== BUG 2 FIX: Endpoint correto da OpenAI =====
                var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);  // ⬅️ CORREÇÃO: Era /responses, deve ser /chat/completions
                // ===== FIM BUG 2 FIX =====
                var body = await response.Content.ReadAsStringAsync();
                var responsePreview = body.Length > 200 ? body.Substring(0, 200) + "..." : body;
                _logger.LogTrace("[Conversa={Conversa}] Resposta bruta da OpenAI (len={Length}): {Preview}", idConversa, body.Length, responsePreview);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI falhou: {Status} {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision("Desculpe, não consegui formular uma resposta agora.", "none", null, false, null);
                }

                // ===== BUG 2 FIX: Parsear resposta correta do /chat/completions =====
                using var doc = JsonDocument.Parse(body);
                var choices = doc.RootElement.GetProperty("choices");

                foreach (var choice in choices.EnumerateArray())
                {
                    var message = choice.GetProperty("message");

                    // Verificar se há tool calls
                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                    {
                        var firstTool = toolCalls[0];
                        var function = firstTool.GetProperty("function");
                        var toolName = function.GetProperty("name").GetString();
                        var args = function.GetProperty("arguments").GetString();

                        _logger.LogInformation(
                            "[Conversa={Conversa}] IA chamou tool: {ToolName}",
                            idConversa,
                            toolName);

                        var result = await _toolExecutor.ExecuteToolAsync(toolName!, args!);

                        return new AssistantDecision(
                            Reply: result,
                            HandoverAction: toolName,
                            AgentPrompt: null,
                            ReservaConfirmada: toolName == "confirmar_reserva",
                            Detalhes: null
                        );
                    }

                    // Mensagem normal com JSON estruturado
                    if (message.TryGetProperty("content", out var contentProp))
                    {
                        var rawJson = contentProp.GetString();

                        if (string.IsNullOrWhiteSpace(rawJson))
                        {
                            _logger.LogError(
                                "[Conversa={Conversa}] IA retornou conteúdo vazio",
                                idConversa);

                            return new AssistantDecision(
                                Reply: "Desculpe, tive um problema ao processar. Pode tentar novamente? 😊",
                                HandoverAction: "none",
                                AgentPrompt: null,
                                ReservaConfirmada: false,
                                Detalhes: null);
                        }

                        try
                        {
                            var decision = JsonSerializer.Deserialize<AssistantDecision>(rawJson, JsonOptions);

                            if (decision == null)
                            {
                                throw new JsonException("Resposta nula após desserialização");
                            }

                            if (string.IsNullOrWhiteSpace(decision.Reply))
                            {
                                throw new JsonException("Campo 'reply' vazio ou nulo");
                            }

                            if (string.IsNullOrWhiteSpace(decision.HandoverAction))
                            {
                                throw new JsonException("Campo 'handover_action' vazio ou nulo");
                            }

                            _logger.LogInformation(
                                "[Conversa={Conversa}] IA processou mensagem com sucesso (action={Action})",
                                idConversa,
                                decision.HandoverAction);

                            return decision;
                        }
                        catch (JsonException ex)
                        {
                            // ===== BUG 2 FIX: NÃO converter texto para JSON =====
                            _logger.LogError(
                                ex,
                                "[Conversa={Conversa}] IA retornou formato inválido. ResponseFormat NÃO está funcionando. Resposta: {Response}",
                                idConversa,
                                TruncarConteudo(rawJson, 300));

                            // Usar fallback conforme especificado na Tarefa 4
                            return new AssistantDecision(
                                Reply: "Desculpe, tive um problema ao processar. Pode tentar novamente? 😊",
                                HandoverAction: "none",
                                AgentPrompt: null,
                                ReservaConfirmada: false,
                                Detalhes: null);
                            // ===== FIM BUG 2 FIX =====
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[Conversa={Conversa}] Erro inesperado ao interpretar JSON da IA",
                                idConversa);

                            return new AssistantDecision(
                                Reply: "Desculpe, ocorreu um erro ao processar sua mensagem. Pode tentar novamente? 😊",
                                HandoverAction: "none",
                                AgentPrompt: null,
                                ReservaConfirmada: false,
                                Detalhes: null);
                        }
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
