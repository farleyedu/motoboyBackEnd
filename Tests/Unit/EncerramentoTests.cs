using System;
using System.IO;
using System.Linq;
using APIBack.DTOs.Delivery;
using APIBack.Model.Enum;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class EncerramentoRulesTests
    {
        private static readonly TimeZoneInfo Brasilia = EncerramentoRules.ResolveTimeZone("America/Sao_Paulo");
        private static readonly TimeSpan Fecha23h = TimeSpan.FromHours(23);

        // Hora local de Brasilia (UTC-3) para UTC.
        private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
            new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

        private static Func<DateOnly, TimeSpan?> TodoDiaFecha(TimeSpan hora) => _ => hora;

        [Fact]
        public void BeforeTheGracePeriodItStillPointsToThePreviousDaysClosing()
        {
            // Fecha 23:00, tolerancia 4 h: o fechamento de 01/10 so conta a partir das 03:00 de 02/10.
            var agora = Local(10, 2, 2, 59);

            var corte = EncerramentoRules.UltimoFechamentoElegivel(TodoDiaFecha(Fecha23h), agora, Brasilia, horasApos: 4);

            Assert.Equal(Local(9, 30, 23), corte);
        }

        [Fact]
        public void AfterTheGracePeriodThePreviousClosingIsTheCutoff()
        {
            var agora = Local(10, 2, 3, 0);

            var corte = EncerramentoRules.UltimoFechamentoElegivel(TodoDiaFecha(Fecha23h), agora, Brasilia, horasApos: 4);

            Assert.Equal(Local(10, 1, 23), corte);
        }

        [Fact]
        public void ClosingTodayCountsOnceItsOwnGracePeriodPassed()
        {
            // Fecha 18:00; as 22:00 do mesmo dia ja passaram as 4 h.
            var agora = Local(10, 1, 22, 0);

            var corte = EncerramentoRules.UltimoFechamentoElegivel(TodoDiaFecha(TimeSpan.FromHours(18)), agora, Brasilia, horasApos: 4);

            Assert.Equal(Local(10, 1, 18), corte);
        }

        [Fact]
        public void ZeroHoursClosesRightAtClosingTime()
        {
            var agora = Local(10, 1, 23, 0);

            var corte = EncerramentoRules.UltimoFechamentoElegivel(TodoDiaFecha(Fecha23h), agora, Brasilia, horasApos: 0);

            Assert.Equal(Local(10, 1, 23), corte);
        }

        [Fact]
        public void ClosedDaysAreSkipped()
        {
            // 01/10 foi folga: o ultimo fechamento valido e o de 30/09.
            Func<DateOnly, TimeSpan?> horario = dia => dia == new DateOnly(2026, 10, 1) ? null : Fecha23h;

            var corte = EncerramentoRules.UltimoFechamentoElegivel(horario, Local(10, 2, 10, 0), Brasilia, horasApos: 4);

            Assert.Equal(Local(9, 30, 23), corte);
        }

        [Fact]
        public void WithoutAnyScheduleThereIsNoCutoff()
        {
            var corte = EncerramentoRules.UltimoFechamentoElegivel(_ => null, Local(10, 2, 10, 0), Brasilia, horasApos: 4);

            Assert.Null(corte);
        }

        [Fact]
        public void OnlyOrdersPlacedUntilTheClosingBelongToTheClosedShift()
        {
            var fechamento = Local(10, 1, 23);

            Assert.True(EncerramentoRules.PertenceAoExpedienteEncerrado(Local(10, 1, 20), fechamento));
            Assert.True(EncerramentoRules.PertenceAoExpedienteEncerrado(fechamento, fechamento));
            Assert.False(EncerramentoRules.PertenceAoExpedienteEncerrado(Local(10, 1, 23, 30), fechamento));
            // Sem horario confiavel nao se encerra: melhor sobrar um do que fechar o pedido errado.
            Assert.False(EncerramentoRules.PertenceAoExpedienteEncerrado(null, fechamento));
        }

        [Theory]
        [InlineData(StatusPedido.Pendente, true)]
        [InlineData(StatusPedido.Atribuido, true)]
        [InlineData(StatusPedido.EmRota, true)]
        [InlineData(StatusPedido.Concluido, false)]
        [InlineData(StatusPedido.Cancelado, false)]
        [InlineData(StatusPedido.Rascunho, false)]
        [InlineData(StatusPedido.EncerradoAuto, false)]
        public void OnlyOpenOrdersAreClosed(StatusPedido status, bool open) =>
            Assert.Equal(open, EncerramentoRules.IsOpen(status));

        [Fact]
        public void OnlyAutoClosedOrdersCanBeReopenedHere()
        {
            Assert.True(EncerramentoRules.CanReopen(StatusPedido.EncerradoAuto));
            Assert.False(EncerramentoRules.CanReopen(StatusPedido.Cancelado));
            Assert.False(EncerramentoRules.CanReopen(StatusPedido.Concluido));
            Assert.False(EncerramentoRules.CanReopen(StatusPedido.Pendente));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(4, true)]
        [InlineData(24, true)]
        [InlineData(-1, false)]
        [InlineData(25, false)]
        public void HoursMustStayBetweenZeroAnd24(int horas, bool valid) =>
            Assert.Equal(valid, EncerramentoRules.IsValidHoras(horas));

        [Fact]
        public void UnknownTimezoneFallsBackToBrasilia()
        {
            Assert.Equal(Brasilia.BaseUtcOffset, EncerramentoRules.ResolveTimeZone("Nao/Existe").BaseUtcOffset);
        }
    }

    public sealed class EncerramentoStatusTests
    {
        [Fact]
        public void AutoClosedHasItsOwnStatusValueAndApiKey()
        {
            Assert.Equal(7, (int)StatusPedido.EncerradoAuto);
            Assert.Equal(StatusPedido.EncerradoAuto, StatusPedidoExtensions.FromDbValue(7));
            Assert.Equal("encerrado_auto", StatusPedido.EncerradoAuto.ToApiKey());
            Assert.Equal("encerrado_auto", StatusPedidoExtensions.ToApiKey(7));
        }

        [Fact]
        public void ListFilterAcceptsTheNewStatus()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Status = "encerrado_auto" });

            Assert.Equal(new[] { 7 }, filtro.Status);
        }

        [Fact]
        public void PublicTrackingTreatsAutoClosedAsFinal()
        {
            var (status, _, final) = PublicTrackingRules.StatusOf(7);

            Assert.Equal("encerrado", status);
            Assert.True(final);
        }

        [Fact]
        public void SimulatorCannotAdvanceAnAutoClosedOrder()
        {
            var erro = SimuladorPedidoRules.ValidateEtapa(SimEtapa.Confirmar, StatusPedido.EncerradoAuto, confirmado: false, preparo: false);

            Assert.NotNull(erro);
            Assert.Contains("encerrado", erro, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class EncerramentoMigrationTests
    {
        private static string Read(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(new[] { dir ?? string.Empty, "Migrations", "Delivery" }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Migration_keeps_history_with_the_motoboy_and_defaults_to_four_hours()
        {
            var sql = Read("20261002_01_encerramento_automatico.sql");

            Assert.Contains("CREATE TABLE IF NOT EXISTS pedido_encerramento", sql);
            Assert.Contains("motoboy_id", sql);
            Assert.Contains("reaberto_em", sql);
            Assert.Contains("ux_pedido_encerramento_aberto", sql);
            Assert.Contains("WHERE reaberto_em IS NULL", sql);
            Assert.Contains("encerramento_auto_ativo BOOLEAN NOT NULL DEFAULT TRUE", sql);
            Assert.Contains("encerramento_auto_horas INTEGER NOT NULL DEFAULT 4", sql);
            Assert.Contains("BETWEEN 0 AND 24", sql);
            Assert.Contains("'expediente', 'manual'", sql);
            Assert.StartsWith("BEGIN;", sql.TrimStart());
            Assert.Contains("COMMIT;", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rollback_undoes_the_migration()
        {
            var sql = Read("rollback", "20261002_01_encerramento_automatico.down.sql");

            Assert.Contains("DROP TABLE IF EXISTS pedido_encerramento", sql);
            Assert.Contains("DROP COLUMN IF EXISTS encerramento_auto_horas", sql);
            Assert.Contains("DROP COLUMN IF EXISTS encerramento_auto_ativo", sql);
        }
    }
}
