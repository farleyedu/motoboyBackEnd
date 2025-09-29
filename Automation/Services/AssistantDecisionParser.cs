// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public static class AssistantDecisionParser
    {
        public static async Task<(bool Success, AssistantDecision Decision, string? ExtractedJson)> TryParse(
            string? conteudo,
            JsonSerializerOptions jsonOptions,
            ILogger logger,
            Guid idConversa,
            IMessageRepository messageRepository)
        {
            if (string.IsNullOrWhiteSpace(conteudo))
            {
                return (false, new AssistantDecision("Resposta vazia da IA.", "none", null, false, null), null);
            }

            string? extractedJson = null;

            try
            {
                // Primeiro tenta interpretar o texto como JSON direto
                var decision = JsonSerializer.Deserialize<AssistantDecision>(conteudo, jsonOptions);
                if (decision != null && !string.IsNullOrWhiteSpace(decision.Reply))
                {
                    return (true, decision, conteudo);
                }
            }
            catch
            {
                // Não é JSON direto, segue para regex
            }

            try
            {
                // Tenta localizar JSON dentro do texto usando regex
                var match = Regex.Match(conteudo, "\\{[\\s\\S]*\\}");
                if (match.Success)
                {
                    extractedJson = match.Value;
                    var decision = JsonSerializer.Deserialize<AssistantDecision>(extractedJson, jsonOptions);
                    if (decision != null && !string.IsNullOrWhiteSpace(decision.Reply))
                    {
                        return (true, decision, extractedJson);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Conversa={Conversa}] Erro ao interpretar JSON retornado pela IA", idConversa);
            }

            // Fallback: se nada deu certo, retorna o texto bruto como reply
            return (false, new AssistantDecision(conteudo, "none", null, false, null), extractedJson);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================
