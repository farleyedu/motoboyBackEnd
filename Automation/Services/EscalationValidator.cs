// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace APIBack.Automation.Services
{
    public class EscalationValidator
    {
        // Frases que EXPLICITAMENTE pedem atendimento humano
        private static readonly string[] ExplicitHumanRequestPhrases = new[]
        {
            "quero falar com",
            "falar com atendente",
            "falar com humano",
            "atendente humano",
            "pessoa real",
            "alguem de verdade",
            "transfere para",
            "me transfere",
            "quero humano",
            "preciso de humano",
            "quero pessoa",
            "atendimento humano",
            "falar com gente",
            "agente humano",
            "operador humano"
        };

        // Palavras que BLOQUEIAM escalação (mesmo que IA tente)
        private static readonly string[] BlockingKeywords = new[]
        {
            "reserva",
            "cancelar",
            "confirmar",
            "horario",
            "data",
            "pessoas",
            "mesa",
            "disponibilidade",
            "cardapio",
            "menu",
            "preco",
            "valor",
            "quanto custa",
            "localizacao",
            "endereco",
            "abre que horas",
            "fecha que horas"
        };

        public static (bool ShouldEscalate, string Reason) ValidateEscalation(
            string userMessage,
            string iaMotivo,
            IEnumerable<string> conversationHistory)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                return (false, "Mensagem do usuário vazia");
            }

            var normalizedMessage = NormalizeText(userMessage);
            var normalizedMotivo = NormalizeText(iaMotivo);

            // 1. Verifica se mensagem do usuário contém pedido EXPLÍCITO
            var hasExplicitRequest = ExplicitHumanRequestPhrases.Any(phrase =>
                normalizedMessage.Contains(NormalizeText(phrase)));

            if (!hasExplicitRequest)
            {
                // Verifica também no motivo fornecido pela IA
                hasExplicitRequest = ExplicitHumanRequestPhrases.Any(phrase =>
                    normalizedMotivo.Contains(NormalizeText(phrase)));
            }

            // 2. Verifica se contém palavras de BLOQUEIO (tópicos que bot resolve)
            var hasBlockingKeyword = BlockingKeywords.Any(keyword =>
                normalizedMessage.Contains(NormalizeText(keyword)));

            // 3. Verifica padrões de frustração genuína
            var frustrationScore = CalculateFrustrationScore(normalizedMessage, conversationHistory);

            // DECISÃO FINAL
            if (hasBlockingKeyword && !hasExplicitRequest)
            {
                return (false, "Usuário está perguntando sobre tópico que bot resolve (reserva, cancelamento, etc)");
            }

            if (hasExplicitRequest)
            {
                return (true, "Usuário pediu explicitamente atendimento humano");
            }

            // Só escala por frustração se score for MUITO alto (>= 3) E não tiver bloqueadores
            if (frustrationScore >= 3 && !hasBlockingKeyword)
            {
                return (true, "Usuário demonstra frustração extrema");
            }

            return (false, "Não há pedido explícito de atendimento humano");
        }

        private static int CalculateFrustrationScore(string message, IEnumerable<string> history)
        {
            var score = 0;
            var historyList = history?.ToList() ?? new List<string>();

            // Frases de frustração forte
            var strongFrustration = new[]
            {
                "nao consigo",
                "nao estou conseguindo",
                "ja tentei",
                "varias vezes",
                "isso nao funciona",
                "nao esta funcionando",
                "desisto",
                "cansado",
                "pessimo",
                "horrivel"
            };

            // Conta quantas frases de frustração aparecem
            score += strongFrustration.Count(phrase => message.Contains(phrase));

            // Se usuário mencionou tentativas repetidas no histórico
            if (historyList.Count >= 5)
            {
                var repeatedIssues = historyList
                    .Skip(Math.Max(0, historyList.Count - 5))
                    .Count(msg => strongFrustration.Any(f => NormalizeText(msg).Contains(f)));

                score += repeatedIssues;
            }

            return score;
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Remove acentos
            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var chars = normalized.Where(c =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark);

            var result = new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);

            // Converte para minúsculas e remove pontuação extra
            result = result.ToLowerInvariant();
            result = Regex.Replace(result, @"[^\w\s]", " ");
            result = Regex.Replace(result, @"\s+", " ");

            return result.Trim();
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) =================