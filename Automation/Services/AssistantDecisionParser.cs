using System;
using System.Collections.Generic;
using System.Text.Json;
using APIBack.Automation.Dtos;

namespace APIBack.Automation.Services
{
    internal static class AssistantDecisionParser
    {
        public static bool TryParse(string? rawContent, JsonSerializerOptions options, out AssistantDecision decision, out string? extractedJson)
        {
            decision = default!;
            extractedJson = ExtractJsonPayload(rawContent);

            if (!string.IsNullOrWhiteSpace(extractedJson))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<AssistantDecisionDto>(extractedJson, options);
                    if (dto != null)
                    {
                        decision = BuildDecisionFromDto(dto);
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // segue para heurísticas
                }
            }

            if (TryInferFromPlainText(rawContent, out decision))
            {
                extractedJson = null;
                return true;
            }

            return false;
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
