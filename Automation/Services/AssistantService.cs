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
        private static readonly string DefaultSystemPrompt = """
Você é o assistente virtual do Bar Seu Eurico via WhatsApp. Seja acolhedor, amigável e use emojis (🌸✨🍻😊) quando adequado.

Sua função é interpretar a intenção do cliente e SEMPRE responder com um JSON estruturado contendo a próxima ação. Você nunca executa ações diretamente; apenas sinaliza o que deve acontecer.

Formato obrigatório:
{
  "acao": "responder" | "confirmar_reserva" | "escalar_para_humano",
  "reply": "...",                    // obrigatório quando acao = "responder"
  "agentPrompt": "..." | null,
  "dadosReserva": {
      "idConversa": "<GUID>",
      "nomeCompleto": "...",
      "qtdPessoas": <int>,
      "data": "...",
      "hora": "HH:mm"
  } | null,
  "escalacao": {
      "idConversa": "<GUID>",
      "motivo": "...",
      "resumoConversa": "..."
  } | null
}

Fluxo obrigatório de reserva:
1. Coletar dados faltantes usando "acao": "responder" (nome, quantidade, data, hora).
2. Assim que possuir todos os dados, responda com um resumo e pergunte se deseja confirmar (ainda usando "responder").
3. Apenas após o cliente confirmar explicitamente, retorne "acao": "confirmar_reserva" com os dados completos e sem mensagem.

Fluxo de escalonamento:
1. Pergunte se deseja falar com um atendente usando "responder".
2. Após confirmação, retorne "acao": "escalar_para_humano" preenchendo "motivo" e "resumoConversa".

Regras adicionais:
- Nunca misture perguntas de confirmação com a execução de ferramentas na mesma resposta.
- Se faltar qualquer informação ou houver dúvida, peça esclarecimentos com "responder".
- Não invente dados; preserve campos ausentes como null.
- Responda sempre em português do Brasil, mantendo tom acolhedor.

Informações do Bar Seu Eurico:
- Endereço: Av. Anselmo Alves dos Santos, 1750 – Bairro Santa Mônica, Uberlândia/MG.
- Horário: Seg-Sex 17h–00h30, Sáb 12h–01h, Dom 12h–00h30. Happy hour: Seg-Sex 17h–20h, Sáb-Dom 12h–16h.
- Diferenciais: Pet friendly 🐶, área kids gratuita 👧🧒, ambiente familiar, promoções (chopp R$4,90, caipirinha R$9,90, batata 30% off) e cardápio com Sra Picanha, Costela Barão, Cupim Bola, Contra Filé, bolinho de costela, frango a passarinho, panelinha do Eurico e diversos drinks.
""";

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<AssistantService> _logger;
        private readonly ToolExecutorService _toolExecutor;

        public AssistantService(
            IHttpClientFactory httpFactory,
            ILogger<AssistantService> logger,
            ToolExecutorService toolExecutor)
        {
            _httpFactory = httpFactory;
            _logger = logger;
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

            var systemPrompt = contexto as string ?? DefaultSystemPrompt;
            var messages = new List<object> { new { role = "system", content = systemPrompt } };

            if (historico != null)
            {
                foreach (var turn in historico)
                {
                    if (string.IsNullOrWhiteSpace(turn.Content))
                    {
                        continue;
                    }

                    var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                    messages.Add(new { role, content = turn.Content });
                }
            }

            messages.Add(new { role = "user", content = textoUsuario });

            var payload = new
            {
                model,
                messages = messages.ToArray(),
                response_format = new { type = "json_object" }
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            _logger.LogInformation("[Conversa={Conversa}] Payload enviado para OpenAI: {Payload}", idConversa, json);

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

                _logger.LogInformation("[Conversa={Conversa}] Resposta bruta da OpenAI: {Body}", idConversa, body);

                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Resposta da OpenAI sem choices", idConversa);
                    return FallbackDecision();
                }

                var messageElement = choices[0].GetProperty("message");
                var messageContent = messageElement.GetProperty("content").GetString();

                if (string.IsNullOrWhiteSpace(messageContent))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Conteúdo vazio retornado pela OpenAI", idConversa);
                    return FallbackDecision();
                }

                IaActionResponse? iaAction;
                try
                {
                    iaAction = JsonSerializer.Deserialize<IaActionResponse>(messageContent, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "[Conversa={Conversa}] Falha ao desserializar resposta da IA: {Content}", idConversa, messageContent);
                    return FallbackDecision(messageContent);
                }

                if (iaAction is null || string.IsNullOrWhiteSpace(iaAction.Acao))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Resposta da IA sem campo 'acao'. Conteúdo: {Content}", idConversa, messageContent);
                    return FallbackDecision(messageContent);
                }

                switch (iaAction.Acao.ToLowerInvariant())
                {
                    case "responder":
                    {
                        var reply = string.IsNullOrWhiteSpace(iaAction.Reply)
                            ? "Desculpe, não entendi sua solicitação. Pode reformular, por favor? 😊"
                            : iaAction.Reply!;

                        return new AssistantDecision(reply, "none", iaAction.AgentPrompt, false, null);
                    }

                    case "confirmar_reserva":
                    {
                        if (iaAction.DadosReserva is null)
                        {
                            _logger.LogWarning("[Conversa={Conversa}] IA sugeriu confirmar reserva sem dados", idConversa);
                            return new AssistantDecision(
                                "Para organizar a sua reserva, preciso que me confirme o nome completo, a quantidade de pessoas, a data e o horário, por favor.",
                                "none",
                                iaAction.AgentPrompt,
                                false,
                                null);
                        }

                        var confirmarArgsJson = JsonSerializer.Serialize(iaAction.DadosReserva, JsonOptions);
                        var confirmarResultado = await _toolExecutor.ExecuteToolAsync("confirmar_reserva", confirmarArgsJson);

                        var reservaConfirmada = false;
                        if (TryExtrairReply(confirmarResultado, out var textoConfirmacao) && !string.IsNullOrWhiteSpace(textoConfirmacao))
                        {
                            reservaConfirmada = textoConfirmacao.Contains("Reserva confirmada", StringComparison.OrdinalIgnoreCase);
                        }

                        return new AssistantDecision(confirmarResultado, "confirmar_reserva", iaAction.AgentPrompt, reservaConfirmada, null);
                    }

                    case "escalar_para_humano":
                    {
                        if (iaAction.Escalacao is null)
                        {
                            _logger.LogWarning("[Conversa={Conversa}] IA sugeriu escalar sem detalhes", idConversa);
                            return new AssistantDecision(
                                "Posso te ajudar com mais alguma informação antes de chamar um atendente humano?",
                                "none",
                                iaAction.AgentPrompt,
                                false,
                                null);
                        }

                        var escalarArgsJson = JsonSerializer.Serialize(iaAction.Escalacao, JsonOptions);
                        var escalarResultado = await _toolExecutor.ExecuteToolAsync("escalar_para_humano", escalarArgsJson);
                        return new AssistantDecision(escalarResultado, "escalar_para_humano", iaAction.AgentPrompt, false, null);
                    }

                    default:
                        _logger.LogWarning("[Conversa={Conversa}] Ação desconhecida sugerida pela IA: {Acao}", idConversa, iaAction.Acao);
                        return FallbackDecision(messageContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro ao chamar OpenAI", idConversa);
                return new AssistantDecision("Desculpe, ocorreu um erro ao gerar a resposta.", "none", null, false, null);
            }
        }

        private bool TryExtrairReply(string json, out string? reply)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("reply", out var replyProperty) && replyProperty.ValueKind == JsonValueKind.String)
                {
                    reply = replyProperty.GetString();
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignora erros de parsing e segue para o fallback
            }

            reply = null;
            return false;
        }

        private AssistantDecision FallbackDecision(string? conteudo = null)
        {
            var mensagemPadrao = "Desculpe, não consegui entender sua solicitação agora. Pode me contar novamente, por favor?";
            if (!string.IsNullOrWhiteSpace(conteudo))
            {
                _logger.LogDebug("Conteúdo recebido no fallback: {Conteudo}", conteudo);
            }

            return new AssistantDecision(mensagemPadrao, "none", null, false, null);
        }

        private sealed class IaActionResponse
        {
            public string? Acao { get; set; }
            public string? Reply { get; set; }
            public string? AgentPrompt { get; set; }
            public ConfirmarReservaArgs? DadosReserva { get; set; }
            public EscalarParaHumanoArgs? Escalacao { get; set; }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================
