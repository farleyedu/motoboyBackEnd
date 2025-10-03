// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Helpers
{
    public static class TimeZoneHelper
    {
        private static readonly string[] SaoPauloTimeZoneIds =
        {
            "America/Sao_Paulo",
            "E. South America Standard Time"
        };

        private static TimeZoneInfo? _cached;

        public static TimeZoneInfo GetSaoPauloTimeZone()
        {
            if (_cached != null)
            {
                return _cached;
            }

            foreach (var id in SaoPauloTimeZoneIds)
            {
                try
                {
                    _cached = TimeZoneInfo.FindSystemTimeZoneById(id);
                    return _cached;
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            _cached = TimeZoneInfo.Local;
            return _cached;
        }

        public static DateTime GetSaoPauloNow()
        {
            var tz = GetSaoPauloTimeZone();
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Utc, tz);
        }

        public static DateTime ConvertUtcToSaoPaulo(DateTime utcDateTime)
        {
            DateTime utc = utcDateTime.Kind switch
            {
                DateTimeKind.Utc => utcDateTime,
                DateTimeKind.Local => utcDateTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc)
            };

            var tz = GetSaoPauloTimeZone();
            return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Utc, tz);
        }

        public static DateTime SpecifyUnspecified(DateTime dateTime)
        {
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
