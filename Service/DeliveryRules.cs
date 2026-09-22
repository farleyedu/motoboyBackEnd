using System;
using System.Globalization;
using System.Linq;

namespace APIBack.Service
{
    /// <summary>
    /// Regras puras do delivery (sem banco), isoladas para teste.
    /// </summary>
    public static class DeliveryRules
    {
        public const int MaxReasonLength = 500;

        // Com fuso explicito (timestamptz::text -> "2025-10-14 12:20:08-03"): instante absoluto.
        private static readonly string[] OffsetFormats =
        {
            "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd HH:mm:ss.FFFFFFFzz",
            "yyyy-MM-dd HH:mm:sszzz",
            "yyyy-MM-dd HH:mm:sszz",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
            "yyyy-MM-ddTHH:mm:ssK",
        };

        // Sem fuso (timestamp ou texto): hora "de parede" do estabelecimento.
        private static readonly string[] LocalFormats =
        {
            "yyyy-MM-dd HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-dd",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy",
        };

        private static readonly string[] TimeOnlyFormats =
        {
            "HH:mm:ss.FFFFFFF",
            "HH:mm:ss",
            "HH:mm",
            "H:mm",
        };

        /// <summary>
        /// Le um horario vindo do banco como texto, qualquer que seja o tipo da coluna
        /// (text, time, timestamp, timestamptz):
        /// - com fuso: devolve o instante em UTC (Kind=Utc, serializado com "Z");
        /// - sem fuso: devolve a hora local do estabelecimento (Kind=Unspecified);
        /// - so horario ("20:58"): ancora na data do pedido; sem ela, null. Nunca assume
        ///   "hoje" -- um pedido de ontem com previsao 20:58 nao pode parecer no prazo hoje;
        /// - valor irreconhecivel: null, em vez de derrubar o endpoint inteiro.
        /// </summary>
        public static DateTime? ParseStoredDateTime(string? raw, DateTime? anchorDate = null)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var value = raw.Trim();

            if (DateTimeOffset.TryParseExact(value, OffsetFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var withOffset))
            {
                return withOffset.UtcDateTime;
            }

            if (DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var local))
            {
                return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            }

            // Horario puro precisa ser tratado antes de qualquer parse generico, que
            // aceitaria "20:58" completando com a data de hoje.
            if (DateTime.TryParseExact(value, TimeOnlyFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowWhiteSpaces, out var timeOnly))
            {
                return anchorDate.HasValue
                    ? DateTime.SpecifyKind(anchorDate.Value.Date + timeOnly.TimeOfDay, DateTimeKind.Unspecified)
                    : null;
            }

            return null;
        }

        /// <summary>
        /// Expressao SQL de "agora" adequada ao tipo real da coluna de horario do pedido.
        /// As colunas legadas guardam hora de parede local; gravar NOW() (UTC no servidor)
        /// numa coluna sem fuso deslocaria os horarios em 3h no painel.
        /// <paramref name="minutesParameter"/> soma minutos (ex.: previsao de entrega).
        /// </summary>
        public static string LocalNowSql(string? postgresDataType, string? minutesParameter = null)
        {
            var interval = string.IsNullOrEmpty(minutesParameter)
                ? string.Empty
                : $" + make_interval(mins => {minutesParameter})";
            var localNow = $"((NOW() AT TIME ZONE '{OperationalDayWindow.TimeZoneId}'){interval})";

            return (postgresDataType ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "timestamp with time zone" => $"(NOW(){interval})",
                "timestamp without time zone" => localNow,
                "time without time zone" or "time with time zone" => $"{localNow}::time",
                "date" => $"{localNow}::date",
                // text/varchar ou desconhecido: texto sem fuso, que o leitor interpreta como local.
                _ => $"to_char({localNow}, 'YYYY-MM-DD HH24:MI:SS')"
            };
        }

        /// <summary>
        /// Compara o codigo de entrega ignorando maiusculas, espacos e hifens
        /// ("ab-12 3" == "AB123").
        /// </summary>
        public static bool DeliveryCodeMatches(string? expected, string? provided)
        {
            var normalizedExpected = NormalizeCode(expected);
            if (normalizedExpected.Length == 0) return true;
            return string.Equals(normalizedExpected, NormalizeCode(provided), StringComparison.Ordinal);
        }

        public static bool HasDeliveryCode(string? code) => NormalizeCode(code).Length > 0;

        private static string NormalizeCode(string? value) =>
            new string((value ?? string.Empty)
                .Where(c => !char.IsWhiteSpace(c) && c != '-')
                .Select(char.ToUpperInvariant)
                .ToArray());

        /// <summary>Motivo obrigatorio (ou opcional), aparado e limitado.</summary>
        public static string? NormalizeReason(string? reason, bool required, string fieldLabel = "motivo")
        {
            var trimmed = reason?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (required)
                {
                    throw new DeliveryDomainException(422, "REASON_REQUIRED", $"Informe o {fieldLabel}.");
                }
                return null;
            }

            if (trimmed.Length > MaxReasonLength)
            {
                throw new DeliveryDomainException(422, "REASON_TOO_LONG", $"O {fieldLabel} excede {MaxReasonLength} caracteres.");
            }

            return trimmed;
        }
    }
}
