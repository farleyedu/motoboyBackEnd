// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace APIBack.Automation.Services
{
    public class ToolExecutorService
    {
        private readonly ILogger<ToolExecutorService> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly HandoverService _handoverService;

        public ToolExecutorService(
            ILogger<ToolExecutorService> logger,
            IConversationRepository conversationRepository,
            HandoverService handoverService)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _handoverService = handoverService;
        }

        /// <summary>
        /// Declara apenas as ferramentas que vamos expor para a IA.
        /// </summary>
        public object[] GetDeclaredTools(Guid idConversa)
        {
            var idConversaString = idConversa.ToString();
            return new object[]
            {
                new {
                    type = "function",
                    name = "confirmar_reserva",
                    description = @"QUANDO USAR: Chame esta ferramenta SOMENTE quando:
1. Você tiver coletado TODAS as informações obrigatórias: nome completo, quantidade de pessoas, data e hora
2. O usuário explicitamente CONFIRMAR que os dados estão corretos (ex: 'sim, está certo', 'confirma', 'pode reservar')
3. Você já tiver apresentado um RESUMO da reserva para o usuário revisar

IMPORTANTE: NÃO chame esta função se:
- Faltar qualquer informação obrigatória
- O usuário não confirmou explicitamente
- Você não apresentou um resumo antes

Esta função confirma definitivamente a reserva e encerra a conversa automaticamente.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new {
                                type = "string",
                                description = "ID único da conversa atual",
                                @enum = new[] { idConversaString }
                            },
                            nomeCompleto = new {
                                type = "string",
                                description = "Nome completo do cliente que fez a reserva"
                            },
                            qtdPessoas = new {
                                type = "integer",
                                description = "Quantidade de pessoas para a reserva"
                            },
                            data = new {
                                type = "string",
                                description = "Data da reserva no formato YYYY-MM-DD"
                            },
                            hora = new {
                                type = "string",
                                description = "Horário da reserva no formato HH:MM"
                            }
                        },
                        required = new[] { "idConversa", "nomeCompleto", "qtdPessoas", "data", "hora" }
                    }
                },
                new {
                    type = "function",
                    name = "escalar_para_humano",
                    description = @"QUANDO USAR: Chame esta ferramenta quando:
1. O usuário SOLICITAR explicitamente falar com um atendente humano (ex: 'quero falar com alguém', 'preciso de ajuda humana')
2. Você não conseguir entender o que o usuário quer APÓS 3 tentativas de esclarecimento
3. O usuário fizer uma solicitação FORA do escopo de reservas (ex: reclamações, cancelamentos, perguntas sobre cardápio detalhado)
4. O usuário demonstrar FRUSTRAÇÃO ou INSATISFAÇÃO clara com o atendimento automatizado

IMPORTANTE: NÃO escale automaticamente só porque:
- O usuário está fornecendo informações aos poucos (isso é normal)
- Há uma pequena dúvida que você pode esclarecer
- O usuário fez uma pergunta simples sobre horários/disponibilidade

Esta função transfere a conversa para um atendente humano imediatamente.",
                    parameters = new {
                        type = "object",
                        properties = new {
                            idConversa = new {
                                type = "string",
                                description = "ID único da conversa atual",
                                @enum = new[] { idConversaString }
                            },
                            motivo = new {
                                type = "string",
                                description = "Breve explicação do motivo do escalonamento para contexto do atendente humano"
                            },
                            resumoConversa = new {
                                type = "string",
                                description = "Resumo breve (2-3 frases) do que foi discutido até agora na conversa"
                            }
                        },
                        required = new[] { "idConversa", "motivo", "resumoConversa" }
                    }
                }
            };
        }

        /// <summary>
        /// Executa a ferramenta chamada pela IA.
        /// </summary>
        public async Task<string> ExecuteToolAsync(string toolName, string argsJson)
        {
            try
            {
                // Se a string JSON estiver escapada (ex: "{\"key\":\"value\"}"), desescape-a.
                if (argsJson.StartsWith("\"") && argsJson.EndsWith("\""))
                {
                    try
                    {
                        argsJson = JsonSerializer.Deserialize<string>(argsJson)!;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Não foi possível desescapar a string JSON. Tentando parsear como está.");
                        // Se falhar, tenta parsear a string original, pode ser que não estivesse duplamente escapada.
                    }
                }

                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                if (args == null)
                {
                    return "Argumentos inválidos.";
                }

                if (!args.TryGetValue("idConversa", out var idConversaElement) ||
                    !Guid.TryParse(idConversaElement.GetString(), out var idConversa))
                {
                    _logger.LogWarning("[Conversa={Conversa}] idConversa inválido ou ausente na chamada da ferramenta '{ToolName}'. Valor recebido: '{ReceivedIdConversa}'", idConversaElement.GetString(), toolName, idConversaElement.GetString());
                    return "ID de conversa inválido.";
                }

                switch (toolName)
                {
                    case "confirmar_reserva":
                        // Validar que todos os dados necessários foram passados
                        if (!args.TryGetValue("nomeCompleto", out var nomeElement) ||
                            !args.TryGetValue("qtdPessoas", out var qtdElement) ||
                            !args.TryGetValue("data", out var dataElement) ||
                            !args.TryGetValue("hora", out var horaElement))
                        {
                            return "Dados incompletos para confirmação. Certifique-se de ter nome completo, quantidade de pessoas, data e hora.";
                        }

                        var nome = nomeElement.GetString();
                        var qtd = qtdElement.GetInt32();
                        var data = dataElement.GetString();
                        var hora = horaElement.GetString();

                        // Montar o HandoverContextDto com os dados da reserva
                        var detalhesReserva = new HandoverContextDto
                        {
                            ClienteNome = nome,
                            NumeroPessoas = qtd.ToString(),
                            Dia = data,
                            Horario = hora
                        };

                        // Aqui você pode salvar os dados da reserva no banco
                        // await _reservaRepository.CriarReservaAsync(idConversa, nome, qtd, data, hora);

                        await _conversationRepository.AtualizarEstadoAsync(idConversa, EstadoConversa.FechadoAutomaticamente);

                        // Enviar notificação para o Telegram sobre a reserva confirmada
                        await _handoverService.ProcessarMensagensTelegramAsync(idConversa, null, true, detalhesReserva);

                        _logger.LogInformation(
                            "[Conversa={Conversa}] Reserva confirmada: {Nome}, {Qtd} pessoas, {Data} às {Hora}",
                            idConversa, nome, qtd, data, hora
                        );
                        return $"✅ Reserva confirmada com sucesso!\n\nNome: {nome}\nPessoas: {qtd}\nData: {data}\nHorário: {hora}\n\nAté breve!";

                    case "escalar_para_humano":
                        var motivoEscalacao = args.TryGetValue("motivo", out var motivoElementEscalacao)
                            ? motivoElementEscalacao.GetString()
                            : "Solicitação do cliente";

                        var resumoConversaEscalacao = args.TryGetValue("resumoConversa", out var resumoElementEscalacao)
                            ? resumoElementEscalacao.GetString()
                            : null;

                        var contextoEscalacao = new HandoverContextDto
                        {
                            Historico = resumoConversaEscalacao != null
                                ? new[] { $"Resumo: {resumoConversaEscalacao}", $"Motivo: {motivoEscalacao}" }
                                : new[] { $"Motivo: {motivoEscalacao}" }
                        };

                        await _conversationRepository.AtualizarEstadoAsync(idConversa, EstadoConversa.EmAtendimento);
                        await _handoverService.ProcessarMensagensTelegramAsync(idConversa, null, false, contextoEscalacao);

                        _logger.LogInformation(
                            "[Conversa={Conversa}] Conversa escalada para humano. Motivo: {Motivo}",
                            idConversa, motivoEscalacao
                        );
                        return $"Transferindo você para um atendente humano.\nEm instantes alguém irá atendê-lo.";

                    default:
                        _logger.LogWarning("[Conversa={Conversa}] Ferramenta desconhecida: {Tool}", idConversa, toolName);
                        return $"Ferramenta {toolName} não implementada.";
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Erro ao fazer parse dos argumentos da ferramenta {Tool}: {Json}", toolName, argsJson);
                return "Erro ao processar os argumentos da ferramenta.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao executar ferramenta {Tool}", toolName);
                return $"Erro ao executar {toolName}: {ex.Message}";
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================