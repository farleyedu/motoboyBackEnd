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
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private const string PromptIndisponivelMensagem = "Ops! Não consegui acessar as orientações do estabelecimento agora. Já pedi ajuda ao time; pode me mandar uma mensagem em alguns minutos?";
        private const string MensagemFallback = "Desculpe, não consegui entender agora. Pode me contar de novo, por favor?";

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
                _logger.LogWarning("[Conversa={Conversa}] OpenAI ApiKey nao configurada; utilizando resposta padrao", idConversa);
                var reply = string.IsNullOrWhiteSpace(textoUsuario) ? "Poderia repetir?" : $"Você disse: '{textoUsuario}'.";
                return new AssistantDecision(reply, "none", null, false, null, null);
            }

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var endpoint = "https://api.openai.com/v1/chat/completions";
            var contextoTexto = (contexto as string)?.Trim();

            if (string.IsNullOrWhiteSpace(contextoTexto))
            {
                _logger.LogWarning("[Conversa={Conversa}] Prompt nao encontrado para o estabelecimento", idConversa);
                return new AssistantDecision(PromptIndisponivelMensagem, "none", null, false, null, null);
            }

            var messages = new List<object> { new { role = "system", content = contextoTexto! } };

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
                    _logger.LogWarning("[Conversa={Conversa}] OpenAI retornou status {Status}: {Body}", idConversa, (int)response.StatusCode, body);
                    return new AssistantDecision(MensagemFallback, "none", null, false, null, null);
                }

                _logger.LogDebug("[Conversa={Conversa}] Resposta bruta da OpenAI: {Body}", idConversa, body);

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Resposta da OpenAI sem choices", idConversa);
                    return FallbackDecision();
                }

                var messageElement = choices[0].GetProperty("message");
                if (!messageElement.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Conteudo vazio retornado pela OpenAI", idConversa);
                    return FallbackDecision();
                }

                var messageContent = contentElement.GetString();
                if (string.IsNullOrWhiteSpace(messageContent))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Conteudo vazio retornado pela OpenAI", idConversa);
                    return FallbackDecision();
                }

                if (!TryParseIaAction(messageContent!, idConversa, out var iaAction, out var decisaoErro))
                {
                    return decisaoErro ?? FallbackDecision(messageContent);
                }

                iaAction.Media = SanitizeMedia(iaAction.Media, idConversa);

                switch (iaAction.Acao?.ToLowerInvariant())
                {
                    case "responder":
                    {
                        var reply = string.IsNullOrWhiteSpace(iaAction.Reply) ? MensagemFallback : iaAction.Reply!;
                        return new AssistantDecision(reply, "none", iaAction.AgentPrompt, false, null, iaAction.Media);
                    }

                    case "confirmar_reserva":
                    {
                        if (iaAction.DadosReserva is null || !iaAction.DadosReserva.PossuiCamposEssenciais())
                        {
                            _logger.LogWarning("[Conversa={Conversa}] IA sugeriu confirmar reserva sem dados suficientes", idConversa);
                            return new AssistantDecision(
                                "Para organizar a sua reserva, preciso do nome completo, quantidade de pessoas, data e horário.",
                                "none",
                                "modelo_reserva",
                                false,
                                null,
                                iaAction.Media);
                        }

                        var dadosReserva = iaAction.DadosReserva;
                        if (!string.IsNullOrWhiteSpace(dadosReserva.IdConversa) && Guid.TryParse(dadosReserva.IdConversa, out var idConversaIa) && idConversaIa != Guid.Empty && idConversaIa != idConversa)
                        {
                            _logger.LogWarning("[Conversa={Conversa}] ID de conversa informado pela IA ({IaId}) nao corresponde ao esperado ({Esperado})", idConversa, idConversaIa, idConversa);
                        }

                        var confirmarArgs = dadosReserva.ToConfirmarReservaArgs(idConversa);
                        var confirmarArgsJson = JsonSerializer.Serialize(confirmarArgs, JsonOptions);
                        var confirmarResultado = await _toolExecutor.ExecuteToolAsync("confirmar_reserva", confirmarArgsJson);

                        var (reply, reservaConfirmada) = ExtrairRespostaDaFerramenta(confirmarResultado);
                        return new AssistantDecision(reply, "confirmar_reserva", null, reservaConfirmada, null, iaAction.Media);
                    }

                    case "escalar_para_humano":
                    {
                        if (iaAction.Escalacao is null || !iaAction.Escalacao.PossuiCamposEssenciais())
                        {
                            _logger.LogWarning("[Conversa={Conversa}] IA sugeriu escalacao sem detalhes", idConversa);
                            return new AssistantDecision(
                                "Entendo! Posso te conectar com um atendente. Se quiser seguir comigo, é só me avisar.",
                                "none",
                                null,
                                false,
                                null,
                                iaAction.Media);
                        }

                        var escalacao = iaAction.Escalacao;
                        if (!string.IsNullOrWhiteSpace(escalacao.IdConversa) && Guid.TryParse(escalacao.IdConversa, out var idConversaIaEscalacao) && idConversaIaEscalacao != Guid.Empty && idConversaIaEscalacao != idConversa)
                        {
                            _logger.LogWarning("[Conversa={Conversa}] ID de conversa informado para escalacao ({IaId}) nao corresponde ao esperado ({Esperado})", idConversa, idConversaIaEscalacao, idConversa);
                        }

                        var escalarArgs = escalacao.ToEscalarArgs(idConversa);
                        var escalarArgsJson = JsonSerializer.Serialize(escalarArgs, JsonOptions);
                        var escalarResultado = await _toolExecutor.ExecuteToolAsync("escalar_para_humano", escalarArgsJson);
                        var (reply, _) = ExtrairRespostaDaFerramenta(escalarResultado);

                        return new AssistantDecision(reply, "escalar_para_humano", null, false, null, iaAction.Media);
                    }

                    default:
                        _logger.LogWarning("[Conversa={Conversa}] Acao desconhecida sugerida pela IA: {Acao}", idConversa, iaAction.Acao);
                        return FallbackDecision(messageContent, iaAction.Media);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Falha ao processar resposta da OpenAI", idConversa);
                return FallbackDecision();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Erro inesperado ao consultar a OpenAI", idConversa);
                return FallbackDecision();
            }
        }

        private bool TryParseIaAction(string jsonContent, Guid idConversa, out IaActionResponse iaAction, out AssistantDecision? decisionErro)
        {
            decisionErro = null;
            iaAction = null!;

            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Resposta da IA nao eh um objeto JSON: {Conteudo}", idConversa, jsonContent);
                    decisionErro = BuildInvalidFormatDecision();
                    return false;
                }

                if (!root.TryGetProperty("acao", out var acaoElement) || acaoElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(acaoElement.GetString()))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Campo 'acao' ausente ou invalido: {Conteudo}", idConversa, jsonContent);
                    decisionErro = BuildInvalidFormatDecision();
                    return false;
                }

                if (!root.TryGetProperty("reply", out var replyElement) || (replyElement.ValueKind != JsonValueKind.String && replyElement.ValueKind != JsonValueKind.Null))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Campo 'reply' ausente ou invalido: {Conteudo}", idConversa, jsonContent);
                    decisionErro = BuildInvalidFormatDecision();
                    return false;
                }

                if (!root.TryGetProperty("dadosReserva", out var dadosReservaElement) || (dadosReservaElement.ValueKind != JsonValueKind.Object && dadosReservaElement.ValueKind != JsonValueKind.Null))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Campo 'dadosReserva' ausente ou invalido: {Conteudo}", idConversa, jsonContent);
                    decisionErro = BuildInvalidFormatDecision();
                    return false;
                }

                if (!root.TryGetProperty("escalacao", out var escalacaoElement) || (escalacaoElement.ValueKind != JsonValueKind.Object && escalacaoElement.ValueKind != JsonValueKind.Null))
                {
                    _logger.LogWarning("[Conversa={Conversa}] Campo 'escalacao' ausente ou invalido: {Conteudo}", idConversa, jsonContent);
                    decisionErro = BuildInvalidFormatDecision();
                    return false;
                }

                if (root.TryGetProperty("media", out var mediaElement) && mediaElement.ValueKind != JsonValueKind.Object && mediaElement.ValueKind != JsonValueKind.Null)
                {
                    _logger.LogWarning("[Conversa={Conversa}] Campo 'media' invalido: {Conteudo}", idConversa, jsonContent);
                    // nao bloqueia a decisao; apenas sinaliza erro e segue sem media
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[Conversa={Conversa}] Resposta da IA nao pode ser lida como JSON: {Conteudo}", idConversa, jsonContent);
                decisionErro = BuildInvalidFormatDecision();
                return false;
            }

            try
            {
                iaAction = JsonSerializer.Deserialize<IaActionResponse>(jsonContent, JsonOptions)
                           ?? throw new JsonException("JSON desserializado para null");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[Conversa={Conversa}] Falha ao desserializar acao da IA: {Conteudo}", idConversa, jsonContent);
                decisionErro = BuildInvalidFormatDecision();
                return false;
            }

            return true;
        }

        private AssistantDecision BuildInvalidFormatDecision()
        {
            return new AssistantDecision(MensagemFallback, "none", null, false, null, null);
        }

        private AssistantDecision FallbackDecision(string? conteudo = null, AssistantMedia? media = null)
        {
            if (!string.IsNullOrWhiteSpace(conteudo))
            {
                _logger.LogDebug("Conteudo recebido no fallback: {Conteudo}", conteudo);
            }

            return new AssistantDecision(MensagemFallback, "none", null, false, null, media);
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

        private AssistantMedia? SanitizeMedia(AssistantMedia? media, Guid idConversa)
        {
            if (media == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(media.Tipo) || string.IsNullOrWhiteSpace(media.Url))
            {
                _logger.LogDebug("[Conversa={Conversa}] Media recebida com campos vazios. Ignorando.", idConversa);
                return null;
            }

            var tipo = media.Tipo.Trim().ToLowerInvariant();
            if (tipo != "imagem" && tipo != "image" && tipo != "pdf" && tipo != "link")
            {
                _logger.LogWarning("[Conversa={Conversa}] Tipo de media desconhecido: {Tipo}", idConversa, media.Tipo);
                return null;
            }

            if (!Uri.TryCreate(media.Url.Trim(), UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("[Conversa={Conversa}] URL de media invalida: {Url}", idConversa, media.Url);
                return null;
            }

            return new AssistantMedia
            {
                Tipo = tipo,
                Url = uri.ToString()
            };
        }

        private sealed class IaActionResponse
        {
            public string? Acao { get; set; }
            public string? Reply { get; set; }
            public string? AgentPrompt { get; set; }
            public DadosReservaPayload? DadosReserva { get; set; }
            public EscalacaoPayload? Escalacao { get; set; }
            public AssistantMedia? Media { get; set; }
        }

        private sealed class DadosReservaPayload
        {
            public string? IdConversa { get; set; }
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

            public ConfirmarReservaArgs ToConfirmarReservaArgs(Guid idConversaAtual)
            {
                Guid idFinal;
                if (!string.IsNullOrWhiteSpace(IdConversa) && Guid.TryParse(IdConversa, out var g) && g != Guid.Empty)
                {
                    idFinal = g;
                }
                else
                {
                    idFinal = idConversaAtual;
                }

                return new ConfirmarReservaArgs
                {
                    IdConversa = idFinal,
                    NomeCompleto = NomeCompleto ?? string.Empty,
                    QtdPessoas = QtdPessoas ?? 0,
                    Data = Data ?? string.Empty,
                    Hora = Hora ?? string.Empty
                };
            }
        }

        private sealed class EscalacaoPayload
        {
            public string? IdConversa { get; set; }
            public string? Motivo { get; set; }
            public string? ResumoConversa { get; set; }

            public bool PossuiCamposEssenciais()
            {
                return !string.IsNullOrWhiteSpace(Motivo)
                    && !string.IsNullOrWhiteSpace(ResumoConversa);
            }

            public EscalarParaHumanoArgs ToEscalarArgs(Guid idConversaAtual)
            {
                Guid idFinal;
                if (!string.IsNullOrWhiteSpace(IdConversa) && Guid.TryParse(IdConversa, out var g) && g != Guid.Empty)
                {
                    idFinal = g;
                }
                else
                {
                    idFinal = idConversaAtual;
                }

                return new EscalarParaHumanoArgs
                {
                    IdConversa = idFinal,
                    Motivo = Motivo ?? string.Empty,
                    ResumoConversa = ResumoConversa ?? string.Empty
                };
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================


