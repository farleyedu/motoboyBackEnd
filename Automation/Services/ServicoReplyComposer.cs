using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Infra.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIBack.Automation.Services
{
    public class ServicoReplyComposer
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _httpFactory;
        private readonly IOptions<OpenAIOptions> _options;
        private readonly ILogger<ServicoReplyComposer> _logger;

        public ServicoReplyComposer(
            IHttpClientFactory httpFactory,
            IOptions<OpenAIOptions> options,
            ILogger<ServicoReplyComposer> logger)
        {
            _httpFactory = httpFactory;
            _options = options;
            _logger = logger;
        }

        public async Task<string> ComposeAsync(Guid idConversa, string factsPrompt, string fallbackReply)
        {
            if (string.IsNullOrWhiteSpace(factsPrompt))
            {
                return fallbackReply;
            }

            var apiKey = _options.Value.ApiKey;
            var model = string.IsNullOrWhiteSpace(_options.Value.Model) ? "gpt-4o-mini" : _options.Value.Model;

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = new string(apiKey.Where(c => !char.IsWhiteSpace(c)).ToArray());
            }

            if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("sk-", StringComparison.Ordinal))
            {
                return fallbackReply;
            }

            var payload = new
            {
                model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Voce redige respostas curtas para atendimento de servicos em portugues do Brasil. " +
                            "Nunca invente fatos. Use apenas os fatos recebidos. " +
                            "Nao adicione preco, duracao, disponibilidade ou promessas que nao estejam autorizadas nos fatos. " +
                            "Retorne JSON estrito no formato {\"reply\":\"...\"}."
                    },
                    new
                    {
                        role = "user",
                        content = factsPrompt
                    }
                },
                max_tokens = 180,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "service_reply",
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                reply = new { type = "string" }
                            },
                            required = new[] { "reply" },
                            additionalProperties = false
                        },
                        strict = true
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);

            try
            {
                var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                client.Timeout = TimeSpan.FromSeconds(20);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[Conversa={Conversa}] Falha ao compor resposta de servicos via OpenAI: {Status}",
                        idConversa,
                        (int)response.StatusCode);
                    return fallbackReply;
                }

                using var document = JsonDocument.Parse(body);
                var choices = document.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0)
                {
                    return fallbackReply;
                }

                var message = choices[0].GetProperty("message");
                if (!message.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
                {
                    return fallbackReply;
                }

                var rawJson = contentProp.GetString();
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return fallbackReply;
                }

                using var replyDoc = JsonDocument.Parse(rawJson);
                if (!replyDoc.RootElement.TryGetProperty("reply", out var replyProp) || replyProp.ValueKind != JsonValueKind.String)
                {
                    return fallbackReply;
                }

                var reply = replyProp.GetString();
                return string.IsNullOrWhiteSpace(reply) ? fallbackReply : reply.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Conversa={Conversa}] Composer de servicos caiu no fallback", idConversa);
                return fallbackReply;
            }
        }
    }
}
