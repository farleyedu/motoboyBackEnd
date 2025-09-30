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
    public class AssistantService : IAssistantService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly string DefaultSystemPrompt = """
Identidade e Objetivo Principal
Você é o assistente virtual do Bar Seu Eurico no WhatsApp. Seja acolhedor, simpático e use emojis (🌸✨🍻😊) quando fizer sentido.

Regra de Ouro
Você nunca executa ações diretamente. Sua função é interpretar a intenção do cliente e responder SEMPRE com um JSON que descreve a próxima etapa.

Formato obrigatório da resposta
{
  "acao": "responder" | "confirmar_reserva" | "escalar_para_humano",
  "reply": "...",            // use null quando não houver mensagem para o cliente
  "dadosReserva": {
    "nomeCompleto": "...",
    "qtdPessoas": 0,
    "data": "...",
    "hora": "HH:mm"
  },
  "escalacao": {
    "motivo": "...",
    "resumoConversa": "..."
  }
}

Fluxo obrigatório de reserva
1. Se faltarem dados (nome, quantidade, data ou hora), use apenas "acao": "responder" para pedir as informações que faltam.
2. Quando tiver todos os dados, use "acao": "responder" para apresentar um resumo e perguntar se a pessoa confirma.
3. Somente depois de o cliente responder afirmativamente, use "acao": "confirmar_reserva" sem mensagem ("reply": null).

Fluxo de escalonamento humano
1. Pergunte primeiro se deseja falar com um atendente usando "acao": "responder".
2. Após o cliente confirmar, use "acao": "escalar_para_humano" e preencha "motivo" e "resumoConversa".

Regras adicionais
- Nunca misture pergunta de confirmação e execução na mesma resposta.
- Não invente dados; mantenha campos vazios ou peça esclarecimentos.
- Responda sempre em português do Brasil com tom acolhedor.

Informações úteis do Bar Seu Eurico
- Endereço: Av. Anselmo Alves dos Santos, 1750 – Bairro Santa Mônica, Uberlândia/MG.
- Horário: Seg-Sex 17h–00h30, Sáb 12h–01h, Dom 12h–00h30. Happy hour: Seg-Sex 17h–20h, Sáb-Dom 12h–16h.
- Diferenciais: Pet friendly 🐶, área kids gratuita 👧🧒, ambiente familiar.
- Promoções: Chopp R$4,90 | Caipirinha R$9,90 | Outras frutas R$13,00 | Batata com 30% off.
- Cardápio resumido: Sra Picanha, Costela Barão, Cupim Bola, Contra Filé, bolinho de costela, frango a passarinho, panelinha do Eurico, cervejas e drinks variados.
""";

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<AssistantService> _logger;
        private readonly ToolExecutorService _toolExecutor;
        private readonly IOptions<OpenAIOptions> _options;

        public AssistantService(
            IHttpClientFactory httpFactory,
            ILogger<AssistantService> logger,
            ToolExecutorService toolExecutor,
            IOptions<OpenAIOptions> options)
        {
            _httpFactory = httpFactory;
            _logger = logger;
            _toolExecutor = toolExecutor;
            _options = options;
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

            var endpoint = "https://api.openai.com/v1/chat/completions";

            var contextoTexto = (contexto as string)?.Trim();
            var systemPrompt = string.IsNullOrWhiteSpace(contextoTexto)
                ? DefaultSystemPrompt
                : string.Concat(contextoTexto, "\n\n", DefaultSystemPrompt);
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
            _logger.LogDebug("[Conversa={Conversa}] Payload enviado para OpenAI: {Payload}", idConversa, json);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(endpoint, content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI falhou: {Status} {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision("Desculpe, não consegui formular uma resposta agora.", "none", null, false, null);
                }

                _logger.LogDebug("[Conversa={Conversa}] Resposta bruta da OpenAI: {Body}", idConversa, body);

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
                                ? "Desculpe, não entendi sua solicitação agora. Pode me contar novamente, por favor? 😊"
                                : iaAction.Reply!;

                            return new AssistantDecision(reply, "none", null, false, null);
                        }

                    case "confirmar_reserva":
                        {
                            if (iaAction.DadosReserva is null || !iaAction.DadosReserva.PossuiCamposEssenciais())
                            {
                                _logger.LogWarning("[Conversa={Conversa}] IA sugeriu confirmar reserva sem dados", idConversa);
                                return new AssistantDecision(
                                    "Para organizar a sua reserva, preciso que me confirme o nome completo, a quantidade de pessoas, a data e o horário, por favor.",
                                    "none",
                                    null,
                                    false,
                                    null);
                            }

                            var confirmarArgs = iaAction.DadosReserva.ToConfirmarReservaArgs(idConversa);
                            var confirmarArgsJson = JsonSerializer.Serialize(confirmarArgs, JsonOptions);
                            var confirmarResultado = await _toolExecutor.ExecuteToolAsync("confirmar_reserva", confirmarArgsJson);

                            var (reply, reservaConfirmada) = ExtrairRespostaDaFerramenta(confirmarResultado);

                            return new AssistantDecision(reply, "confirmar_reserva", null, reservaConfirmada, null);
                        }

                    case "escalar_para_humano":
                        {
                            if (iaAction.Escalacao is null)
                            {
                                _logger.LogWarning("[Conversa={Conversa}] IA sugeriu escalar sem detalhes", idConversa);
                                return new AssistantDecision(
                                    "Posso te ajudar com mais alguma informação antes de chamar um atendente humano?",
                                    "none",
                                    null,
                                    false,
                                    null);
                            }

                            var escalarArgs = iaAction.Escalacao.ToEscalarArgs(idConversa);

                            if (string.IsNullOrWhiteSpace(escalarArgs.Motivo) || string.IsNullOrWhiteSpace(escalarArgs.ResumoConversa))
                            {
                                _logger.LogWarning("[Conversa={Conversa}] IA tentou escalar sem motivo ou resumo válidos", idConversa);
                                return new AssistantDecision(
                                    "Claro! Antes de te conectar, pode me contar rapidinho o motivo do atendimento?",
                                    "none",
                                    null,
                                    false,
                                    null);
                            }

                            var escalarArgsJson = JsonSerializer.Serialize(escalarArgs, JsonOptions);
                            var escalarResultado = await _toolExecutor.ExecuteToolAsync("escalar_para_humano", escalarArgsJson);
                            var (reply, _) = ExtrairRespostaDaFerramenta(escalarResultado);

                            return new AssistantDecision(reply, "escalar_para_humano", null, false, null);
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

        private (string Reply, bool ReservaConfirmada) ExtrairRespostaDaFerramenta(string json)
        {
            string? reply = null;
            var reservaConfirmada = false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("reply", out var replyProperty) && replyProperty.ValueKind == JsonValueKind.String)
                {
                    reply = replyProperty.GetString();
                }

                if (doc.RootElement.TryGetProperty("reserva_confirmada", out var confirmadaProperty) && confirmadaProperty.ValueKind == JsonValueKind.True)
                {
                    reservaConfirmada = true;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Falha ao extrair reply do JSON retornado pela ferramenta");
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = json;
            }

            return (reply!, reservaConfirmada);
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
            public DadosReservaPayload? DadosReserva { get; set; }
            public EscalacaoPayload? Escalacao { get; set; }
        }

        private sealed class DadosReservaPayload
        {
            public string? NomeCompleto { get; set; }
            public int? QtdPessoas { get; set; }
            public string? Data { get; set; }
            public string? Hora { get; set; }

            public bool PossuiCamposEssenciais()
            {
                return !string.IsNullOrWhiteSpace(NomeCompleto)
                    && QtdPessoas.HasValue
                    && !string.IsNullOrWhiteSpace(Data)
                    && !string.IsNullOrWhiteSpace(Hora);
            }

            public ConfirmarReservaArgs ToConfirmarReservaArgs(Guid idConversa)
            {
                return new ConfirmarReservaArgs
                {
                    IdConversa = idConversa,
                    NomeCompleto = NomeCompleto ?? string.Empty,
                    QtdPessoas = QtdPessoas ?? 0,
                    Data = Data ?? string.Empty,
                    Hora = Hora ?? string.Empty
                };
            }
        }

        private sealed class EscalacaoPayload
        {
            public string? Motivo { get; set; }
            public string? ResumoConversa { get; set; }

            public EscalarParaHumanoArgs ToEscalarArgs(Guid idConversa)
            {
                return new EscalarParaHumanoArgs
                {
                    IdConversa = idConversa,
                    Motivo = Motivo ?? string.Empty,
                    ResumoConversa = ResumoConversa ?? string.Empty
                };
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================