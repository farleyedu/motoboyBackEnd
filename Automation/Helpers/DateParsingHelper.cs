using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace APIBack.Automation.Helpers
{
    /// <summary>
    /// Utilitários para extração de componentes de data a partir de texto livre.
    /// O texto recebido deve ser normalizado (minúsculas, sem acentos) antes de chamar os métodos.
    /// </summary>
    public static class DateParsingHelper
    {
        /// <summary>
        /// Tenta extrair um número de dia (1-31) do texto normalizado.
        /// Prioriza padrões com a palavra "dia" e, na ausência, procura números isolados não associados a horas.
        /// </summary>
        public static bool TryExtractDayNumber(string normalizedText, out int day)
        {
            day = 0;

            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return false;
            }

            // Prioriza padrões com a palavra "dia"
            var matchDia = Regex.Match(normalizedText, @"dia\s*(\d{1,2})\b");
            if (matchDia.Success && int.TryParse(matchDia.Groups[1].Value, out var diaNumero) && IsValidDay(diaNumero))
            {
                day = diaNumero;
                return true;
            }

            // Se não encontrou "dia", procura números isolados (1-31) que não façam parte de horas
            foreach (Match match in Regex.Matches(normalizedText, @"(?<!\d)(\d{1,2})(?!\d)"))
            {
                if (!int.TryParse(match.Value, out diaNumero) || !IsValidDay(diaNumero))
                {
                    continue;
                }

                var before = match.Index > 0 ? normalizedText[match.Index - 1] : '\0';
                var afterIndex = match.Index + match.Length;
                var after = afterIndex < normalizedText.Length ? normalizedText[afterIndex] : '\0';

                // Ignora números colados em horário (ex: 19:00) ou em palavras como "#25"
                if (before == ':' || after == ':' || before == '#' || after == '#')
                {
                    continue;
                }

                day = diaNumero;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Remove acentuação do texto para facilitar matching.
        /// </summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString()
                .Normalize(NormalizationForm.FormC)
                .ToLowerInvariant()
                .Trim();
        }

        private static bool IsValidDay(int dia) => dia is >= 1 and <= 31;
    }
}
