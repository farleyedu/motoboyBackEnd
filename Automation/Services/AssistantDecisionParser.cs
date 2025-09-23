using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using APIBack.Automation.Dtos;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    internal static class AssistantDecisionParser
    {
        public static bool TryParse(string? rawContent, JsonSerializerOptions options, out AssistantDecision decision, out string? extractedJson, ILogger? logger = null, Guid? idConversa = null)
        {
            decision = default!;
            
            // LOG 1: Conteúdo bruto da OpenAI
            logger?.LogInformation("[Conversa={Conversa}] [DEBUG] Conteúdo bruto da OpenAI: {RawContent}", 
                idConversa, rawContent ?? "NULL");
            
            extractedJson = ExtractJsonPayload(rawContent);
            
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
                        
                        decision = BuildDecisionFromDto(dto);
                        
                        // LOG 4: Objeto desserializado final
                        logger?.LogInformation("[Conversa={Conversa}] [DEBUG] AssistantDecision desserializado - Reply: '{Reply}', HandoverAction: '{HandoverAction}', AgentPrompt: '{AgentPrompt}', ReservaConfirmada: {ReservaConfirmada}", 
                            idConversa, decision.Reply, decision.HandoverAction, decision.AgentPrompt ?? "NULL", decision.ReservaConfirmada);
                        
                        return true;
                    }
                }
                catch (JsonException ex)
                {
                    logger?.LogWarning(ex, "[Conversa={Conversa}] [DEBUG] Erro ao desserializar JSON: {JsonError}", idConversa, ex.Message);
                    // segue para heurísticas
                }
            }

            if (TryInferFromPlainText(rawContent, out decision))
            {
                logger?.LogInformation("[Conversa={Conversa}] [DEBUG] Decisão inferida do texto plano - Reply: '{Reply}', HandoverAction: '{HandoverAction}'", 
                    idConversa, decision.Reply, decision.HandoverAction);
                extractedJson = null;
                return true;
            }

            logger?.LogWarning("[Conversa={Conversa}] [DEBUG] Falha ao interpretar resposta da OpenAI", idConversa);
            return false;
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

        private static AssistantDecision BuildDecisionFromDto(AssistantDecisionDto dto)
        {
            var reply = dto.reply ?? string.Empty;
            var action = NormalizeHandoverAction(dto);
            var agentPrompt = string.IsNullOrWhiteSpace(dto.agent_prompt) ? null : dto.agent_prompt.Trim();
            var confirmada = dto.reserva_confirmada ?? false;
            return new AssistantDecision(reply, action, agentPrompt, confirmada, dto.detalhes);
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
