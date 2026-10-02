using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Model.Enum;
using APIBack.Repository.Interface;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class OfertaRotaRulesTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        private static RouteStopDto Stop(int pedidoId, int position) => new() { PedidoId = pedidoId, Position = position, Status = "offered" };

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(60, true)]
        [InlineData(0, false)]
        [InlineData(61, false)]
        public void PrazoPrecisaFicarEntre1e60Minutos(int minutos, bool valido) =>
            Assert.Equal(valido, OfertaRotaRules.IsValidPrazo(minutos));

        [Fact]
        public void OfertaVenceNoPrazoSemResposta()
        {
            Assert.False(OfertaRotaRules.Expirou(T0, T0.AddSeconds(119), 2));
            Assert.True(OfertaRotaRules.Expirou(T0, T0.AddMinutes(2), 2));
            Assert.True(OfertaRotaRules.Expirou(T0, T0.AddMinutes(10), 2));
        }

        [Fact]
        public void OfertaMostraAsParadasEmOrdemEOPrazo()
        {
            var offerId = Guid.NewGuid();
            var offer = OfertaRotaRules.BuildOffer(new[] { Stop(9, 3), Stop(7, 1), Stop(8, 2) }, offerId, T0, 2);

            Assert.NotNull(offer);
            Assert.Equal(new[] { 7, 8, 9 }, offer!.Stops.Select(s => s.PedidoId));
            Assert.Equal(offerId, offer.OfferId);
            Assert.Equal(T0.AddMinutes(2), offer.ExpiresAtUtc);
            Assert.Equal(2, offer.TimeoutMinutes);
        }

        [Fact]
        public void SemParadasOuSemHorarioNaoHaOferta()
        {
            Assert.Null(OfertaRotaRules.BuildOffer(Array.Empty<RouteStopDto>(), null, T0, 2));
            Assert.Null(OfertaRotaRules.BuildOffer(new[] { Stop(1, 1) }, null, null, 2));
        }
    }

    public sealed class OfertaRotaStatusTests
    {
        [Fact]
        public void AguardandoMotoboyTemValorEChaveProprios()
        {
            Assert.Equal(8, (int)StatusPedido.AguardandoMotoboy);
            Assert.Equal(StatusPedido.AguardandoMotoboy, StatusPedidoExtensions.FromDbValue(8));
            Assert.Equal("aguardando_motoboy", StatusPedido.AguardandoMotoboy.ToApiKey());
        }

        [Fact]
        public void EEstadoInternoDePedidoEmAberto()
        {
            // O encerramento automatico alcanca o pedido que ficou esperando o aceite.
            Assert.True(EncerramentoRules.IsOpen(StatusPedido.AguardandoMotoboy));
        }

        [Fact]
        public void FiltroDaListaAceitaOStatusNovo()
        {
            var filtro = PedidoFiltro.From(new PedidoFiltroRequest { Status = "aguardando_motoboy" });

            Assert.Equal(new[] { 8 }, filtro.Status);
        }
    }

    public sealed class OfertaRotaServiceTests
    {
        private readonly Mock<IPedidoQueueRepository> _repository = new();

        private PedidoQueueService Create() => new(_repository.Object);

        [Fact]
        public async Task EnviarRotaExigeMotoboyEPedidos()
        {
            var service = Create();

            var semMotoboy = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AssignRouteAsync(Guid.NewGuid(), 1, 0, new List<int> { 5 }));
            var semPedidos = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AssignRouteAsync(Guid.NewGuid(), 1, 10, new List<int>()));
            var repetidos = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AssignRouteAsync(Guid.NewGuid(), 1, 10, new List<int> { 5, 5 }));

            Assert.Equal("INVALID_REQUEST", semMotoboy.Code);
            Assert.Equal("INVALID_REQUEST", semPedidos.Code);
            Assert.Equal("INVALID_REQUEST", repetidos.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task EnviarRotaRepassaOrdemEscolhida()
        {
            var est = Guid.NewGuid();
            _repository.Setup(r => r.AssignRouteAsync(est, 1, 10, It.IsAny<IReadOnlyList<int>>())).ReturnsAsync(new MotoboyQueueDto());

            await Create().AssignRouteAsync(est, 1, 10, new List<int> { 3, 1, 2 });

            _repository.Verify(r => r.AssignRouteAsync(est, 1, 10, It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 3, 1, 2 }))), Times.Once);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(61)]
        public async Task ConfiguracaoRejeitaPrazoForaDeFaixa(int minutos)
        {
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                Create().UpdateSettingsAsync(Guid.NewGuid(), 1, new UpdateDeliverySettingsRequest { TransferPolicy = "direct", OfferTimeoutMinutes = minutos }));

            Assert.Equal("INVALID_OFFER_TIMEOUT", error.Code);
        }

        [Fact]
        public async Task RecusarNormalizaOMotivo()
        {
            var est = Guid.NewGuid();
            _repository.Setup(r => r.RejectOfferAsync(est, 10, It.IsAny<string?>())).ReturnsAsync(new MotoboyQueueDto());

            await Create().RejectOfferAsync(est, 10, "   ");

            _repository.Verify(r => r.RejectOfferAsync(est, 10, null), Times.Once);
        }
    }

    public sealed class OfertaRotaMigrationTests
    {
        private static string Read(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(new[] { dir ?? string.Empty, "Migrations", "Delivery" }.Concat(parts).ToArray()));
        }

        [Fact]
        public void MigrationNaoMudaOFluxoPadrao()
        {
            var sql = Read("20261002_02_confirmacao_motoboy.sql");

            Assert.Contains("offered_at_utc TIMESTAMPTZ", sql);
            Assert.Contains("offer_id UUID", sql);
            // Desligado por padrao: ninguem muda de comportamento ate ligar a confirmacao.
            Assert.Contains("require_motoboy_acceptance BOOLEAN NOT NULL DEFAULT FALSE", sql);
            Assert.Contains("offer_timeout_minutes INTEGER NOT NULL DEFAULT 2", sql);
            Assert.Contains("BETWEEN 1 AND 60", sql);
            Assert.StartsWith("BEGIN;", sql.TrimStart());
            Assert.Contains("COMMIT;", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RollbackDesfazAMigration()
        {
            var sql = Read("rollback", "20261002_02_confirmacao_motoboy.down.sql");

            Assert.Contains("DROP COLUMN IF EXISTS offered_at_utc", sql);
            Assert.Contains("DROP COLUMN IF EXISTS require_motoboy_acceptance", sql);
        }
    }
}
