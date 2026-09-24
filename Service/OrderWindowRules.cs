using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using APIBack.DTOs.Delivery;

namespace APIBack.Service
{
    /// <summary>Intervalo resolvido, em UTC. Ponta nula = aberta.</summary>
    public readonly record struct OrderWindowRange(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc)
    {
        public bool Contains(DateTimeOffset instant) =>
            (FromUtc is null || instant >= FromUtc) && (ToUtc is null || instant <= ToUtc);
    }

    /// <summary>
    /// Validacao e calculo da janela de pedidos do mapa. Sem banco, para poder testar.
    /// Todos os horarios da configuracao sao de Brasilia (o fuso do dia operacional).
    /// </summary>
    public static class OrderWindowRules
    {
        public const int MaxHours = 720;
        private const int MaxShifts = 14;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static OrderWindowDto Default() => new();

        public static string Serialize(OrderWindowDto window) => JsonSerializer.Serialize(window, Json);

        /// <summary>Le o JSON salvo; qualquer problema cai no padrao em vez de derrubar o mapa.</summary>
        public static OrderWindowDto Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Default();
            try
            {
                var window = JsonSerializer.Deserialize<OrderWindowDto>(raw, Json);
                return window == null ? Default() : Validate(window);
            }
            catch (Exception ex) when (ex is JsonException or DeliveryDomainException)
            {
                return Default();
            }
        }

        public static OrderWindowDto Validate(OrderWindowDto? window)
        {
            if (window == null) return Default();

            var mode = (window.Mode ?? string.Empty).Trim().ToLowerInvariant();
            if (!OrderWindowModes.IsValid(mode))
            {
                throw Invalid($"Modo da janela invalido. Use '{OrderWindowModes.LastHours}', '{OrderWindowModes.OperationalDay}', '{OrderWindowModes.Shifts}' ou '{OrderWindowModes.Custom}'.");
            }

            var result = new OrderWindowDto { Mode = mode, AlwaysShowOpenOrders = window.AlwaysShowOpenOrders };

            switch (mode)
            {
                case OrderWindowModes.LastHours:
                    if (window.Hours is null or < 1 or > MaxHours)
                    {
                        throw Invalid($"Informe as horas da janela, de 1 a {MaxHours}.");
                    }
                    result.Hours = window.Hours;
                    break;

                case OrderWindowModes.Shifts:
                    if (window.Shifts == null || window.Shifts.Count == 0)
                    {
                        throw Invalid("Informe pelo menos uma faixa de horario.");
                    }
                    if (window.Shifts.Count > MaxShifts)
                    {
                        throw Invalid($"No maximo {MaxShifts} faixas de horario.");
                    }
                    result.Shifts = window.Shifts.Select(ValidateShift).ToList();
                    break;

                case OrderWindowModes.Custom:
                    var from = ParseLocal(window.CustomFrom, "inicio");
                    var to = ParseLocal(window.CustomTo, "fim");
                    if (from is null && to is null)
                    {
                        throw Invalid("Informe o inicio, o fim ou os dois.");
                    }
                    if (from is not null && to is not null && to <= from)
                    {
                        throw Invalid("O fim deve ser depois do inicio.");
                    }
                    result.CustomFrom = from?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    result.CustomTo = to?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    break;
            }

            return result;
        }

        private static OrderWindowShiftDto ValidateShift(OrderWindowShiftDto? shift)
        {
            if (shift == null) throw Invalid("Faixa de horario invalida.");
            var days = (shift.Days ?? new List<int>()).Distinct().OrderBy(day => day).ToList();
            if (days.Count == 0 || days.Any(day => day is < 0 or > 6))
            {
                throw Invalid("Cada faixa precisa de dias da semana entre 0 (domingo) e 6 (sabado).");
            }
            var start = ParseClock(shift.Start, "inicio da faixa");
            var end = ParseClock(shift.End, "fim da faixa");
            if (start == end)
            {
                throw Invalid("A faixa nao pode terminar no mesmo horario em que comeca.");
            }
            return new OrderWindowShiftDto { Days = days, Start = start.ToString("HH:mm", CultureInfo.InvariantCulture), End = end.ToString("HH:mm", CultureInfo.InvariantCulture) };
        }

        /// <summary>O intervalo de criacao de pedidos que o mapa deve mostrar agora.</summary>
        public static OrderWindowRange Resolve(OrderWindowDto? window, DateTimeOffset nowUtc)
        {
            window ??= Default();
            switch (window.Mode)
            {
                case OrderWindowModes.LastHours when window.Hours is > 0:
                    return new OrderWindowRange(nowUtc.AddHours(-window.Hours.Value), null);

                case OrderWindowModes.Shifts when window.Shifts.Count > 0:
                    return ResolveShifts(window.Shifts, nowUtc) ?? OperationalDay(nowUtc);

                case OrderWindowModes.Custom:
                    var from = TryParseLocal(window.CustomFrom);
                    var to = TryParseLocal(window.CustomTo);
                    if (from is null && to is null) return OperationalDay(nowUtc);
                    return new OrderWindowRange(
                        from is null ? null : OperationalDayWindow.LocalToUtc(from.Value),
                        to is null ? null : OperationalDayWindow.LocalToUtc(to.Value));

                default:
                    return OperationalDay(nowUtc);
            }
        }

        private static OrderWindowRange OperationalDay(DateTimeOffset nowUtc) =>
            new(OperationalDayWindow.ToUtcRange(OperationalDayWindow.Today(nowUtc)).FromUtc, null);

        /// <summary>
        /// A faixa em andamento ou, fora de todas, a ultima que ja terminou: assim o painel
        /// nao esvazia no minuto em que o turno fecha. Null se nenhuma ocorreu nos ultimos 8 dias.
        /// </summary>
        private static OrderWindowRange? ResolveShifts(IReadOnlyList<OrderWindowShiftDto> shifts, DateTimeOffset nowUtc)
        {
            var today = OperationalDayWindow.Today(nowUtc);
            OrderWindowRange? best = null;
            DateTimeOffset bestStart = DateTimeOffset.MinValue;

            for (var back = 0; back <= 7; back++)
            {
                var date = today.AddDays(-back);
                var weekday = (int)date.DayOfWeek;
                foreach (var shift in shifts.Where(item => item.Days.Contains(weekday)))
                {
                    var start = TimeOnly.ParseExact(shift.Start, "HH:mm", CultureInfo.InvariantCulture);
                    var end = TimeOnly.ParseExact(shift.End, "HH:mm", CultureInfo.InvariantCulture);
                    var startUtc = OperationalDayWindow.LocalToUtc(date.ToDateTime(start));
                    if (startUtc > nowUtc) continue;
                    var endDate = end <= start ? date.AddDays(1) : date;
                    var endUtc = OperationalDayWindow.LocalToUtc(endDate.ToDateTime(end));
                    if (startUtc > bestStart)
                    {
                        bestStart = startUtc;
                        // Em andamento: sem fim (pedidos novos continuam entrando).
                        best = new OrderWindowRange(startUtc, endUtc > nowUtc ? null : endUtc);
                    }
                }
            }
            return best;
        }

        /// <summary>Data/hora de criacao do pedido como instante UTC (o banco guarda hora de Brasilia sem fuso).</summary>
        public static DateTimeOffset? PlacedAtUtc(DateTime? value)
        {
            if (value is null) return null;
            return value.Value.Kind == DateTimeKind.Utc
                ? new DateTimeOffset(value.Value)
                : OperationalDayWindow.LocalToUtc(value.Value);
        }

        // ---- Leitura de textos ------------------------------------------------------------

        private static TimeOnly ParseClock(string? value, string field)
        {
            if (!TimeOnly.TryParseExact((value ?? string.Empty).Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                throw Invalid($"Horario invalido em {field}. Use HH:mm (ex.: 18:30).");
            }
            return time;
        }

        private static DateTime? TryParseLocal(string? value) =>
            DateTime.TryParseExact((value ?? string.Empty).Trim(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

        private static DateTime? ParseLocal(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return TryParseLocal(value)
                ?? throw Invalid($"Data e hora invalidas em {field}. Use yyyy-MM-ddTHH:mm (ex.: 2026-09-24T18:00).");
        }

        private static DeliveryDomainException Invalid(string message) =>
            new(422, "INVALID_ORDER_WINDOW", message);
    }
}
