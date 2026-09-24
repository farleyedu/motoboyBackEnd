using System;
using System.Collections.Generic;
using APIBack.DTOs.Delivery;
using APIBack.DTOs.Tracking;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class OrderWindowRulesTests
    {
        // Quinta-feira 24/09/2026, 21:00 em Brasilia (00:00 UTC do dia 25).
        private static readonly DateTimeOffset Now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        private static DateTimeOffset Brasilia(int day, int hour, int minute = 0, int month = 9) =>
            new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

        [Fact]
        public void Default_IsOperationalDayFromMidnightWithOpenOrdersAlwaysShown()
        {
            var window = OrderWindowRules.Default();
            var range = OrderWindowRules.Resolve(window, Now);

            Assert.Equal(OrderWindowModes.OperationalDay, window.Mode);
            Assert.True(window.AlwaysShowOpenOrders);
            Assert.Equal(Brasilia(24, 0), range.FromUtc);
            Assert.Null(range.ToUtc);
        }

        [Fact]
        public void LastHours_LooksBackFromNow()
        {
            var range = OrderWindowRules.Resolve(new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = 10 }, Now);
            Assert.Equal(Now.AddHours(-10), range.FromUtc);
            Assert.Null(range.ToUtc);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(721)]
        public void LastHours_RejectsOutOfRange(int hours)
        {
            var error = Assert.Throws<DeliveryDomainException>(() =>
                OrderWindowRules.Validate(new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = hours }));
            Assert.Equal("INVALID_ORDER_WINDOW", error.Code);
        }

        private static OrderWindowDto Night() => new()
        {
            Mode = OrderWindowModes.Shifts,
            // Segunda a sexta (a faixa COMECA nesses dias): 18:00 ate 02:00 do dia seguinte.
            Shifts = new List<OrderWindowShiftDto> { new() { Days = new List<int> { 1, 2, 3, 4, 5 }, Start = "18:00", End = "02:00" } },
        };

        [Fact]
        public void Shifts_InProgress_StartsAtShiftStartAndStaysOpen()
        {
            // Quinta 21:00: a faixa de quinta comecou as 18:00 e ainda nao terminou.
            var range = OrderWindowRules.Resolve(Night(), Now);
            Assert.Equal(Brasilia(24, 18), range.FromUtc);
            Assert.Null(range.ToUtc);
        }

        [Fact]
        public void Shifts_CrossingMidnight_StillBelongsToTheDayItStarted()
        {
            // Sexta 00:30 (quinta para sexta): ainda e a faixa que comecou na quinta.
            var range = OrderWindowRules.Resolve(Night(), Brasilia(25, 0, 30));
            Assert.Equal(Brasilia(24, 18), range.FromUtc);
            Assert.Null(range.ToUtc);
        }

        [Fact]
        public void Shifts_AfterClosing_KeepsTheLastShiftWithItsEnd()
        {
            // Sexta 10:00: fora de qualquer faixa; vale a de quinta, que terminou as 02:00 de sexta.
            var range = OrderWindowRules.Resolve(Night(), Brasilia(25, 10));
            Assert.Equal(Brasilia(24, 18), range.FromUtc);
            Assert.Equal(Brasilia(25, 2), range.ToUtc);
        }

        [Fact]
        public void Shifts_WeekendGap_FallsBackToFridayShift()
        {
            // Domingo 12:00: a ultima faixa que houve foi a de sexta (18:00 ate 02:00 de sabado).
            var range = OrderWindowRules.Resolve(Night(), Brasilia(27, 12));
            Assert.Equal(Brasilia(25, 18), range.FromUtc);
            Assert.Equal(Brasilia(26, 2), range.ToUtc);
        }

        [Fact]
        public void Shifts_TwoRangesInTheSameDay_PicksTheOneInProgress()
        {
            var window = new OrderWindowDto
            {
                Mode = OrderWindowModes.Shifts,
                Shifts = new List<OrderWindowShiftDto>
                {
                    new() { Days = new List<int> { 4 }, Start = "11:00", End = "15:00" },
                    new() { Days = new List<int> { 4 }, Start = "18:00", End = "23:00" },
                },
            };
            Assert.Equal(Brasilia(24, 18), OrderWindowRules.Resolve(window, Now).FromUtc);
            Assert.Equal(Brasilia(24, 11), OrderWindowRules.Resolve(window, Brasilia(24, 13)).FromUtc);
        }

        [Fact]
        public void Custom_UsesTheExactInstantsInBrasiliaTime()
        {
            var window = OrderWindowRules.Validate(new OrderWindowDto
            {
                Mode = OrderWindowModes.Custom, CustomFrom = "2026-09-24T18:00", CustomTo = "2026-09-25T02:00",
            });
            var range = OrderWindowRules.Resolve(window, Now);
            Assert.Equal(Brasilia(24, 18), range.FromUtc);
            Assert.Equal(Brasilia(25, 2), range.ToUtc);
            Assert.True(range.Contains(Brasilia(24, 23)));
            Assert.False(range.Contains(Brasilia(25, 3)));
        }

        [Theory]
        [InlineData("2026-09-25T02:00", "2026-09-24T18:00")]
        [InlineData("amanha", null)]
        [InlineData(null, null)]
        public void Custom_RejectsBackwardsOrUnreadableRanges(string? from, string? to)
        {
            Assert.Throws<DeliveryDomainException>(() =>
                OrderWindowRules.Validate(new OrderWindowDto { Mode = OrderWindowModes.Custom, CustomFrom = from, CustomTo = to }));
        }

        [Theory]
        [InlineData("25:00", "02:00", 1)]
        [InlineData("18:00", "18:00", 1)]
        [InlineData("18:00", "02:00", 9)]
        public void Shifts_RejectInvalidTimesAndDays(string start, string end, int day)
        {
            Assert.Throws<DeliveryDomainException>(() => OrderWindowRules.Validate(new OrderWindowDto
            {
                Mode = OrderWindowModes.Shifts,
                Shifts = new List<OrderWindowShiftDto> { new() { Days = new List<int> { day }, Start = start, End = end } },
            }));
        }

        [Fact]
        public void Parse_FallsBackToDefaultOnGarbageOrInvalidJson()
        {
            Assert.Equal(OrderWindowModes.OperationalDay, OrderWindowRules.Parse("not json").Mode);
            Assert.Equal(OrderWindowModes.OperationalDay, OrderWindowRules.Parse("{\"mode\":\"last_hours\",\"hours\":0}").Mode);
            Assert.Equal(OrderWindowModes.OperationalDay, OrderWindowRules.Parse(null).Mode);
        }

        [Fact]
        public void SerializeThenParse_RoundTrips()
        {
            var window = OrderWindowRules.Validate(Night());
            window.AlwaysShowOpenOrders = false;
            var back = OrderWindowRules.Parse(OrderWindowRules.Serialize(window));
            Assert.Equal(OrderWindowModes.Shifts, back.Mode);
            Assert.False(back.AlwaysShowOpenOrders);
            Assert.Equal("18:00", back.Shifts[0].Start);
            Assert.Equal(new List<int> { 1, 2, 3, 4, 5 }, back.Shifts[0].Days);
        }

        // ---- Recorte do map-state ---------------------------------------------------------

        private static OrderMapDto Order(string status, DateTime? placed, DateTimeOffset? completed = null) => new()
        {
            StatusPedido = status,
            HorarioPedido = placed,
            CompletedAtUtc = completed,
        };

        private static DateTime BrasiliaLocal(int day, int hour) => new(2026, 9, day, hour, 0, 0, DateTimeKind.Unspecified);

        [Fact]
        public void MapFilter_OpenOrderOlderThanWindowStaysWhenAlwaysShown()
        {
            var window = new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = 10, AlwaysShowOpenOrders = true };
            var range = OrderWindowRules.Resolve(window, Now);
            Assert.True(TrackingRepository.IsInOrderWindow(Order("pendente", BrasiliaLocal(20, 9)), window, range));

            window.AlwaysShowOpenOrders = false;
            Assert.False(TrackingRepository.IsInOrderWindow(Order("pendente", BrasiliaLocal(20, 9)), window, range));
        }

        [Fact]
        public void MapFilter_RecentOrderInsideWindowIsShown()
        {
            var window = new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = 10, AlwaysShowOpenOrders = false };
            var range = OrderWindowRules.Resolve(window, Now);
            Assert.True(TrackingRepository.IsInOrderWindow(Order("pendente", BrasiliaLocal(24, 15)), window, range));
        }

        [Fact]
        public void MapFilter_FinishedOrderCountsWhenItsDeliveryFellInsideTheWindow()
        {
            var window = OrderWindowRules.Default();
            var range = OrderWindowRules.Resolve(window, Now);
            // Feito ontem as 23:00, entregue hoje as 00:20: continua aparecendo hoje.
            Assert.True(TrackingRepository.IsInOrderWindow(Order("concluido", BrasiliaLocal(23, 23), Brasilia(24, 0, 20)), window, range));
            // Feito e entregue anteontem: fora.
            Assert.False(TrackingRepository.IsInOrderWindow(Order("concluido", BrasiliaLocal(22, 12), Brasilia(22, 13)), window, range));
        }

        [Fact]
        public void MapFilter_OrderWithoutAnyTimestampIsNeverHidden()
        {
            var window = new OrderWindowDto { Mode = OrderWindowModes.LastHours, Hours = 1, AlwaysShowOpenOrders = false };
            Assert.True(TrackingRepository.IsInOrderWindow(Order("pendente", null), window, OrderWindowRules.Resolve(window, Now)));
        }
    }
}
