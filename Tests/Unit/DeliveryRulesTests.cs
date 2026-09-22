using System;
using APIBack.Infrastructure.Logging;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DeliveryRulesTests
    {
        // ---- Horarios lidos como texto ---------------------------------------------

        [Fact]
        public void ParseStoredDateTime_WithOffset_ReturnsUtcInstant()
        {
            var result = DeliveryRules.ParseStoredDateTime("2025-10-14 12:20:08-03");

            Assert.NotNull(result);
            Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
            Assert.Equal(new DateTime(2025, 10, 14, 15, 20, 8, DateTimeKind.Utc), result.Value);
        }

        [Fact]
        public void ParseStoredDateTime_WithFractionAndOffset_ReturnsUtcInstant()
        {
            var result = DeliveryRules.ParseStoredDateTime("2025-10-14 12:20:08.123456-03");

            Assert.Equal(new DateTime(2025, 10, 14, 15, 20, 8, DateTimeKind.Utc).AddTicks(1234560), result);
        }

        [Fact]
        public void ParseStoredDateTime_WithoutOffset_KeepsLocalWallTime()
        {
            var result = DeliveryRules.ParseStoredDateTime("2025-09-09 00:43:06");

            Assert.Equal(DateTimeKind.Unspecified, result!.Value.Kind);
            Assert.Equal(new DateTime(2025, 9, 9, 0, 43, 6), result.Value);
        }

        [Fact]
        public void ParseStoredDateTime_TimeOnly_IsAnchoredOnOrderDate()
        {
            var result = DeliveryRules.ParseStoredDateTime("20:58", new DateTime(2024, 1, 15));

            Assert.Equal(new DateTime(2024, 1, 15, 20, 58, 0), result);
        }

        [Fact]
        public void ParseStoredDateTime_TimeOnlyWithoutAnchor_IsNullNotToday()
        {
            // Sem a data do pedido, "20:58" nao pode virar "hoje as 20:58".
            Assert.Null(DeliveryRules.ParseStoredDateTime("20:58"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nao e horario")]
        [InlineData("19h58")]
        public void ParseStoredDateTime_UnrecognizedValue_IsNullInsteadOfThrowing(string? raw)
        {
            Assert.Null(DeliveryRules.ParseStoredDateTime(raw, new DateTime(2024, 1, 15)));
        }

        [Fact]
        public void ParseStoredDateTime_DateOnly_IsMidnight()
        {
            Assert.Equal(new DateTime(2024, 1, 15), DeliveryRules.ParseStoredDateTime("2024-01-15"));
        }

        // ---- Codigo de entrega -------------------------------------------------------

        [Theory]
        [InlineData("AB123", "ab123")]
        [InlineData("AB-123", "ab 1 2 3")]
        [InlineData(" 4521 ", "4521")]
        public void DeliveryCodeMatches_IgnoresCaseSpacesAndHyphens(string expected, string provided)
        {
            Assert.True(DeliveryRules.DeliveryCodeMatches(expected, provided));
        }

        [Fact]
        public void DeliveryCodeMatches_RejectsDifferentCode()
        {
            Assert.False(DeliveryRules.DeliveryCodeMatches("4521", "4522"));
            Assert.False(DeliveryRules.DeliveryCodeMatches("4521", null));
        }

        [Fact]
        public void DeliveryCodeMatches_NoExpectedCode_AlwaysAccepts()
        {
            Assert.True(DeliveryRules.DeliveryCodeMatches(null, "qualquer"));
            Assert.True(DeliveryRules.DeliveryCodeMatches("  ", null));
            Assert.False(DeliveryRules.HasDeliveryCode(" - "));
        }

        // ---- Motivo ------------------------------------------------------------------

        [Fact]
        public void NormalizeReason_RequiredAndMissing_Throws()
        {
            var exception = Assert.Throws<DeliveryDomainException>(() => DeliveryRules.NormalizeReason("  ", required: true));
            Assert.Equal("REASON_REQUIRED", exception.Code);
        }

        [Fact]
        public void NormalizeReason_TooLong_Throws()
        {
            var exception = Assert.Throws<DeliveryDomainException>(() =>
                DeliveryRules.NormalizeReason(new string('x', DeliveryRules.MaxReasonLength + 1), required: false));
            Assert.Equal("REASON_TOO_LONG", exception.Code);
        }

        [Fact]
        public void NormalizeReason_TrimsAndAllowsEmptyWhenOptional()
        {
            Assert.Equal("Cliente ausente", DeliveryRules.NormalizeReason("  Cliente ausente ", required: true));
            Assert.Null(DeliveryRules.NormalizeReason(null, required: false));
        }

        // ---- "Agora" por tipo de coluna ---------------------------------------------

        [Fact]
        public void LocalNowSql_Timestamptz_UsesAbsoluteNow()
        {
            Assert.Equal("(NOW())", DeliveryRules.LocalNowSql("timestamp with time zone"));
        }

        [Fact]
        public void LocalNowSql_ColumnsWithoutTimezone_UseBrasiliaWallTime()
        {
            Assert.Contains("AT TIME ZONE 'America/Sao_Paulo'", DeliveryRules.LocalNowSql("timestamp without time zone"));
            Assert.EndsWith("::time", DeliveryRules.LocalNowSql("time without time zone"));
            Assert.EndsWith("::date", DeliveryRules.LocalNowSql("date"));
            Assert.StartsWith("to_char(", DeliveryRules.LocalNowSql("text"));
            Assert.StartsWith("to_char(", DeliveryRules.LocalNowSql(null));
        }

        [Fact]
        public void LocalNowSql_AddsMinutesParameter()
        {
            Assert.Contains("make_interval(mins => @PrevisaoMinutos)",
                DeliveryRules.LocalNowSql("timestamp with time zone", "@PrevisaoMinutos"));
        }

        // ---- Dia operacional ---------------------------------------------------------

        [Fact]
        public void OperationalDayWindow_IsBrasiliaDayInUtc()
        {
            var (fromUtc, toUtc) = OperationalDayWindow.ToUtcRange(new DateOnly(2026, 9, 22));

            Assert.Equal(new DateTimeOffset(2026, 9, 22, 3, 0, 0, TimeSpan.Zero), fromUtc);
            Assert.Equal(new DateTimeOffset(2026, 9, 23, 3, 0, 0, TimeSpan.Zero), toUtc);
        }

        [Fact]
        public void OperationalDayWindow_Today_UsesBrasiliaDate()
        {
            // 01:00 UTC ainda e o dia anterior em Brasilia (22:00).
            Assert.Equal(new DateOnly(2026, 9, 21),
                OperationalDayWindow.Today(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero)));
        }

        // ---- Log sem token -----------------------------------------------------------

        [Fact]
        public void SensitiveQueryString_RedactsAccessToken()
        {
            var redacted = SensitiveQueryStringEnricher.Redact("?id=abc&access_token=eyJhbGciOi.xyz.123");

            Assert.Equal("?id=abc&access_token=[redacted]", redacted);
        }

        [Fact]
        public void SensitiveQueryString_KeepsOtherParameters()
        {
            Assert.Equal("?negotiateVersion=1", SensitiveQueryStringEnricher.Redact("?negotiateVersion=1"));
        }
    }
}
