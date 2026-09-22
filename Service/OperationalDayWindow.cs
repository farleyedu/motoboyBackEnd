using System;

namespace APIBack.Service
{
    /// <summary>
    /// "Dia" operacional do delivery no fuso do Brasil (America/Sao_Paulo). O servidor
    /// roda em UTC; sem isso o "hoje" das metricas e do trajeto virava as 21h locais.
    /// </summary>
    public static class OperationalDayWindow
    {
        public const string TimeZoneId = "America/Sao_Paulo";

        private static readonly Lazy<TimeZoneInfo> Zone = new(ResolveZone);

        private static TimeZoneInfo ResolveZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
            }
            catch (Exception) when (OperatingSystem.IsWindows())
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
                }
                catch (Exception)
                {
                    return FixedBrasilia();
                }
            }
            catch (Exception)
            {
                // Imagem sem tzdata: Brasilia nao tem horario de verao desde 2019.
                return FixedBrasilia();
            }
        }

        private static TimeZoneInfo FixedBrasilia() =>
            TimeZoneInfo.CreateCustomTimeZone("BRT-fixed", TimeSpan.FromHours(-3), "Brasilia", "Brasilia");

        public static DateOnly Today(DateTimeOffset nowUtc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Zone.Value).DateTime);

        /// <summary>Intervalo [inicio, fim) do dia local, em UTC.</summary>
        public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ToUtcRange(DateOnly localDate)
        {
            return (ToUtc(localDate), ToUtc(localDate.AddDays(1)));
        }

        private static DateTimeOffset ToUtc(DateOnly localDate)
        {
            var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var offset = Zone.Value.GetUtcOffset(localStart);
            return new DateTimeOffset(localStart, offset).ToUniversalTime();
        }
    }
}
