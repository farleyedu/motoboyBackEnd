using System;
using System.Threading.Tasks;
using APIBack.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class GoogleUsoContadorTests
    {
        private sealed class RelogioFalso : TimeProvider
        {
            public DateTimeOffset Agora { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Agora;
        }

        private static GoogleUsoContador Novo(RelogioFalso relogio) =>
            new(null, relogio, NullLogger<GoogleUsoContador>.Instance);

        [Fact]
        public void Month_and_day_keys_follow_utc()
        {
            var agora = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-3));

            Assert.Equal("2026-11", GoogleUsoContador.Mes(agora));
            Assert.Equal("2026-11-01", GoogleUsoContador.Dia(agora));
        }

        [Fact]
        public async Task Without_database_the_daily_fallback_stops_at_monthly_limit_divided_by_31()
        {
            var contador = Novo(new RelogioFalso());

            // 62 / 31 = 2 por dia.
            Assert.True(await contador.TentarReservarAsync("geocoding", 62));
            Assert.True(await contador.TentarReservarAsync("geocoding", 62));
            Assert.False(await contador.TentarReservarAsync("geocoding", 62));
        }

        [Fact]
        public async Task The_daily_fallback_starts_over_the_next_day()
        {
            var relogio = new RelogioFalso();
            var contador = Novo(relogio);
            Assert.True(await contador.TentarReservarAsync("geocoding", 31));
            Assert.False(await contador.TentarReservarAsync("geocoding", 31));

            relogio.Agora = relogio.Agora.AddDays(1);

            Assert.True(await contador.TentarReservarAsync("geocoding", 31));
        }

        [Fact]
        public async Task Services_are_counted_separately()
        {
            var contador = Novo(new RelogioFalso());
            Assert.True(await contador.TentarReservarAsync("geocoding", 31));
            Assert.False(await contador.TentarReservarAsync("geocoding", 31));

            Assert.True(await contador.TentarReservarAsync("navegacao", 31));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task A_non_positive_limit_blocks_everything(int limite)
        {
            Assert.False(await Novo(new RelogioFalso()).TentarReservarAsync("geocoding", limite));
        }

        [Fact]
        public async Task Summary_reports_what_the_fallback_counted()
        {
            var contador = Novo(new RelogioFalso());
            await contador.TentarReservarAsync("geocoding", 9000);
            await contador.TentarReservarAsync("geocoding", 9000);

            var resumo = await contador.ObterAsync("geocoding", 9000);

            Assert.Equal(2, resumo.Usado);
            Assert.Equal(9000, resumo.Limite);
            Assert.Equal("2026-10", resumo.Mes);
        }
    }
}
