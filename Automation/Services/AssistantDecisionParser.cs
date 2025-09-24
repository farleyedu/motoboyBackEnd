using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using APIBack.Automation.Dtos;
using Microsoft.Extensions.Logging;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    internal static class AssistantDecisionParser
    {
        public record AssistantDecisionParseResult(bool Success, AssistantDecision Decision, string? ExtractedJson);

        public static async Task<AssistantDecisionParseResult> TryParse(string? rawContent, JsonSerializerOptions options, ILogger? logger = null, Guid? idConversa = null, IMessageRepository? messageRepository = null)
        {
            // LOG 1: Conteúdo bruto da OpenAI
            logger?.LogInformation("[Conversa={Conversa}] [DEBUG] Conteúdo bruto da OpenAI: {RawContent}", 
                idConversa, rawContent ?? "NULL");
            
            var extractedJson = ExtractJsonPayload(rawContent);
            
            // LOG 2: JSON extraído
            logger?.LogInformation("[Conversa={Conversa}] [DEBUG] JSON extraído: {ExtractedJson}", 
                idConversa, extractedJson ?? "NULL");

            if (!string.IsNullOrWhiteSpace(extractedJson))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<AssistantDecisionDto>(extractedJson, options);
                    if (dto != null)
                    {
                        // LOG 3: Verificação de campos alternativos para handoverAction
                        LogHandoverActionAnalysis(dto, logger, idConversa);
                        
                        var decision = await BuildDecisionFromDto(dto, messageRepository, idConversa);
                        
                        // LOG 4: Objeto desserializado final
                        logger?.LogInformation("[Conversa={Conversa}] [DEBUG] AssistantDecision desserializado - Reply: '{Reply}', HandoverAction: '{HandoverAction}', AgentPrompt: '{AgentPrompt}', ReservaConfirmada: {ReservaConfirmada}", 
                            idConversa, decision.Reply, decision.HandoverAction, decision.AgentPrompt ?? "NULL", decision.ReservaConfirmada);
                        
                        return new AssistantDecisionParseResult(true, decision, extractedJson);
                    }
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex, "[Conversa={Conversa}] [DEBUG] Erro ao desserializar JSON: {JsonError}", idConversa, ex.Message);
                    // segue para heurísticas
                }
            }

            if (TryInferFromPlainText(rawContent, out var inferredDecision))
            {
                logger?.LogInformation("[Conversa={Conversa}] [DEBUG] Decisão inferida do texto plano - Reply: '{Reply}', HandoverAction: '{HandoverAction}'", 
                    idConversa, inferredDecision.Reply, inferredDecision.HandoverAction);
                return new AssistantDecisionParseResult(true, inferredDecision, null);
            }

            logger?.LogWarning("[Conversa={Conversa}] [DEBUG] Falha ao interpretar resposta da OpenAI", idConversa);
            return new AssistantDecisionParseResult(false, default!, null);
        }
        
        private static void LogHandoverActionAnalysis(AssistantDecisionDto dto, ILogger? logger, Guid? idConversa)
        {
            if (logger == null) return;
            
            // Analisa todos os possíveis campos de handover
            var handoverFields = new Dictionary<string, string?>
            {
                ["handover"] = dto.handover,
                ["handoverAction"] = dto.handoverAction,
                ["handover_action"] = dto.handover_action
            };
            
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] Análise de campos handover no DTO:", idConversa);
            
            foreach (var field in handoverFields)
            {
                logger.LogInformation("[Conversa={Conversa}] [DEBUG] - {FieldName}: '{FieldValue}'", 
                    idConversa, field.Key, field.Value ?? "NULL");
            }
            
            // Log dos novos campos
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] Novos campos capturados:", idConversa);
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] - nome_completo: '{NomeCompleto}'", idConversa, dto.nome_completo ?? "NULL");
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] - qtd_pessoas: '{QtdPessoas}'", idConversa, dto.qtd_pessoas.HasValue ? dto.qtd_pessoas.Value.ToString() : "NULL");
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] - data: '{Data}'", idConversa, dto.data ?? "NULL");
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] - hora: '{Hora}'", idConversa, dto.hora ?? "NULL");
            
            // Verifica se há outros campos que possam ser handoverAction com nomes diferentes
            var allProperties = typeof(AssistantDecisionDto).GetProperties();
            var knownHandoverFields = new[] { "handover", "handoverAction", "handover_action" };
            
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] Todos os campos do DTO:", idConversa);
            foreach (var prop in allProperties)
            {
                var value = prop.GetValue(dto)?.ToString();
                var isHandoverField = knownHandoverFields.Contains(prop.Name);
                logger.LogInformation("[Conversa={Conversa}] [DEBUG] - {PropertyName}: '{PropertyValue}' {IsHandoverField}", 
                    idConversa, prop.Name, value ?? "NULL", isHandoverField ? "(CAMPO HANDOVER)" : "");
            }
            
            // Verifica se o JSON bruto contém campos handover não mapeados
            // Isso seria útil se houvesse campos como "handover", "handover_action" etc. não capturados
            var finalAction = NormalizeHandoverAction(dto);
            logger.LogInformation("[Conversa={Conversa}] [DEBUG] HandoverAction final normalizado: '{FinalAction}'", 
                idConversa, finalAction);
        }

        private static async Task<AssistantDecision> BuildDecisionFromDto(AssistantDecisionDto dto, IMessageRepository? messageRepository, Guid? idConversa)
        {
            var handoverContext = new HandoverContextDto
            {
                ClienteNome = dto.nome_completo,
                NumeroPessoas = dto.qtd_pessoas.HasValue ? dto.qtd_pessoas.Value.ToString() : null,
                Dia = dto.data,
                Horario = dto.hora,
                Telefone = dto?.detalhes?.Telefone,
                Historico = null // Initialize as null, will be populated if messages are found
            };

            if (messageRepository is not null && idConversa is not null)
            {
                var messages = await messageRepository.GetByConversationAsync(idConversa.Value);
                handoverContext.Historico = messages
                    .OrderBy(m => m.DataHora)
                    .Select(m => $"{(m.Direcao == DirecaoMensagem.Entrada ? "Cliente" : "Assistente")}: {m.Conteudo}")
                    .ToList();
            }

            return new AssistantDecision(
                dto?.reply ?? string.Empty,
                NormalizeHandoverAction(dto),
                string.IsNullOrWhiteSpace(dto.agent_prompt) ? null : dto.agent_prompt.Trim(),
                dto.reserva_confirmada ?? false,
                handoverContext
            );
        }

        private static string? ExtractJsonPayload(string? rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return null;
            }

            var trimmed = rawContent.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    return trimmed.Substring(start, end - start + 1);
                }

                return null;
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return null;
        }

        private static bool TryInferFromPlainText(string? rawContent, out AssistantDecision decision)
        {
            decision = default!;
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return false;
            }

            var normalized = rawContent.ToLowerInvariant();
            if (normalized.Contains("reserva registrada"))
            {
                var agentPrompt = MontarResumoReserva(rawContent);
                decision = new AssistantDecision(rawContent.Trim(), "confirm", agentPrompt, true, null);
                return true;
            }

            return false;
        }

        private static string MontarResumoReserva(string conteudo)
        {
            var partes = new List<string>();
            AdicionarParte(conteudo, "Nome:", partes, "Nome");
            AdicionarParte(conteudo, "Número de pessoas:", partes, "Pessoas");
            AdicionarParte(conteudo, "Dia:", partes, "Dia");
            AdicionarParte(conteudo, "Horário:", partes, "Horário");

            if (partes.Count == 0)
            {
                return "Nova reserva confirmada.";
            }

            return "Nova reserva confirmada: " + string.Join(", ", partes) + ".";
        }

        private static void AdicionarParte(string texto, string rotulo, List<string> partes, string prefixo)
        {
            var valor = ExtrairValorAposRotulo(texto, rotulo);
            if (!string.IsNullOrWhiteSpace(valor))
            {
                partes.Add($"{prefixo} {valor}");
            }
        }

        private static string? ExtrairValorAposRotulo(string texto, string rotulo)
        {
            var index = texto.IndexOf(rotulo, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            index += rotulo.Length;
            while (index < texto.Length && char.IsWhiteSpace(texto[index]))
            {
                index++;
            }

            var fim = texto.IndexOfAny(new[] { '\r', '\n' }, index);
            var valor = fim >= 0 ? texto[index..fim] : texto[index..];
            return valor.Trim();
        }

        private static string NormalizeHandoverAction(AssistantDecisionDto dto)
        {
            var raw = dto.handover;
            if (string.IsNullOrWhiteSpace(raw)) raw = dto.handoverAction;
            if (string.IsNullOrWhiteSpace(raw)) raw = dto.handover_action;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return "none";
            }

            var normalized = raw.Trim().ToLowerInvariant();
            return normalized switch
            {
                "confirm" => "confirm",
                "ask" => "ask",
                _ => "none"
            };
        }
    }
}
