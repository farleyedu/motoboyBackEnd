using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace APIBack.Service
{
    internal static class ValidationUtils
    {
        internal static string? TrimToNull(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        internal static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        internal static List<string> NormalizeStringList(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Select(TrimToNull)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static int NormalizePage(int page)
            => page <= 0 ? 1 : page;

        internal static int NormalizePageSize(int pageSize, int defaultValue = 20, int maxValue = 100)
        {
            if (pageSize <= 0)
            {
                return defaultValue;
            }

            return Math.Min(pageSize, maxValue);
        }

        internal static long? ParseNullableLong(string? value)
        {
            var normalized = TrimToNull(value);
            return long.TryParse(normalized, out var parsed) ? parsed : null;
        }

        internal static void AddError(Dictionary<string, List<string>> errors, string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = new List<string>();
                errors[field] = list;
            }

            if (!list.Any(item => string.Equals(item, message, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(message);
            }
        }

        internal static void ThrowIfAny(Dictionary<string, List<string>> errors, string message = "Dados invalidos.")
        {
            if (errors.Count > 0)
            {
                throw new RequestValidationException(message, errors);
            }
        }
    }
}
