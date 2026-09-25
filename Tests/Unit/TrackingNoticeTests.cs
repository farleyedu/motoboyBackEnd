using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Rastreio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class TrackingEtaTests
    {
        [Fact]
        public void Uses_measured_speed_within_bounds()
        {
            // 1 km reto, 10 m/s: 1000*1.3/10 = 130 s ~ 2,17 min
            Assert.Equal(1000 * 1.3 / 10 / 60, TrackingEta.Minutes(1000, 10), 5);
        }

        [Fact]
        public void Stopped_or_slow_uses_default_speed_not_infinity()
        {
            var expected = 1000 * 1.3 / TrackingEta.DefaultSpeedMps / 60;

            Assert.Equal(expected, TrackingEta.Minutes(1000, 0), 5);
            Assert.Equal(expected, TrackingEta.Minutes(1000, null), 5);
            Assert.Equal(expected, TrackingEta.Minutes(1000, 0.4), 5);
        }

        [Fact]
        public void Speed_is_floored_and_capped()
        {
            Assert.Equal(1000 * 1.3 / TrackingEta.MinSpeedMps / 60, TrackingEta.Minutes(1000, 1.2), 5);
            Assert.Equal(1000 * 1.3 / TrackingEta.MaxSpeedMps / 60, TrackingEta.Minutes(1000, 60), 5);
        }
    }

    public class ArrivingRulesTests
    {
        private static NoticeSettings Settings(int minutes = 5, int radius = 400, bool enabled = true) =>
            new() { ArrivingMinutes = minutes, ArrivingRadiusM = radius, ArrivingEnabled = enabled };

        private static (ArrivingVerdict, string?) Eval(bool shares, double? distance, double? age, double? speed, NoticeSettings? settings = null) =>
            ArrivingRules.Evaluate(new ArrivingInput(shares, distance, age, speed, settings ?? Settings()));

        [Fact]
        public void Motoboy_who_did_not_authorize_never_qualifies()
        {
            Assert.Equal((ArrivingVerdict.Skip, "motoboy_nao_autorizou_localizacao"), Eval(false, 100, 5, 8));
        }

        [Fact]
        public void Disabled_notice_or_missing_or_old_position_is_skipped()
        {
            Assert.Equal((ArrivingVerdict.Skip, "aviso_desligado"), Eval(true, 100, 5, 8, Settings(enabled: false)));
            Assert.Equal((ArrivingVerdict.Skip, "sem_posicao"), Eval(true, null, null, 8));
            Assert.Equal((ArrivingVerdict.Skip, "posicao_desatualizada"), Eval(true, 100, 300, 8));
        }

        [Fact]
        public void Far_away_is_not_yet_and_close_qualifies_by_eta()
        {
            // 8 m/s: 1200 m * 1.3 / 8 = 195 s = 3,25 min (<= 5); 3000 m = 8,1 min
            Assert.Equal(ArrivingVerdict.Qualifies, Eval(true, 1200, 10, 8).Item1);
            Assert.Equal(ArrivingVerdict.NotYet, Eval(true, 3000, 10, 8).Item1);
        }

        [Fact]
        public void Without_reliable_speed_the_fallback_radius_decides()
        {
            Assert.Equal(ArrivingVerdict.Qualifies, Eval(true, 350, 10, null).Item1);
            Assert.Equal(ArrivingVerdict.NotYet, Eval(true, 450, 10, 0).Item1);
            Assert.Equal(ArrivingVerdict.Qualifies, Eval(true, 900, 10, null, Settings(radius: 1000)).Item1);
        }

        [Fact]
        public void Configured_minutes_change_the_threshold()
        {
            Assert.Equal(ArrivingVerdict.NotYet, Eval(true, 1200, 10, 8, Settings(minutes: 2)).Item1);
            Assert.Equal(ArrivingVerdict.Qualifies, Eval(true, 1200, 10, 8, Settings(minutes: 4)).Item1);
        }
    }

    public class ArrivingHysteresisTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Single_sample_does_not_fire_but_sustained_qualification_does()
        {
            var h = new ArrivingHysteresis(TimeSpan.FromSeconds(20));

            Assert.False(h.Observe(1, true, T0));
            Assert.False(h.Observe(1, true, T0.AddSeconds(10)));
            Assert.True(h.Observe(1, true, T0.AddSeconds(20)));
        }

        [Fact]
        public void Oscillation_resets_the_clock()
        {
            var h = new ArrivingHysteresis(TimeSpan.FromSeconds(20));

            h.Observe(1, true, T0);
            Assert.False(h.Observe(1, false, T0.AddSeconds(15)));
            Assert.False(h.Observe(1, true, T0.AddSeconds(25)));
            Assert.False(h.Observe(1, true, T0.AddSeconds(40)));
            Assert.True(h.Observe(1, true, T0.AddSeconds(45)));
        }

        [Fact]
        public void Orders_are_tracked_independently_and_can_be_forgotten()
        {
            var h = new ArrivingHysteresis(TimeSpan.FromSeconds(10));

            h.Observe(1, true, T0);
            Assert.False(h.Observe(2, true, T0.AddSeconds(5)));
            Assert.True(h.Observe(1, true, T0.AddSeconds(10)));
            h.Forget(1);
            Assert.False(h.Observe(1, true, T0.AddSeconds(11)));
        }
    }

    public class NoticeSettingsRulesTests
    {
        [Fact]
        public void Bounds_and_defaults()
        {
            Assert.Equal(5, NoticeSettingsRules.NormalizeMinutes(null));
            Assert.Equal(400, NoticeSettingsRules.NormalizeRadius(null));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeMinutes(0));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeMinutes(61));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeRadius(49));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeRadius(5001));
        }

        [Fact]
        public void Template_blank_returns_default_and_unknown_variable_is_refused()
        {
            Assert.Null(NoticeSettingsRules.NormalizeTemplate("   "));
            Assert.Equal("Oi {cliente}", NoticeSettingsRules.NormalizeTemplate("  Oi {cliente} "));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeTemplate("Oi {apelido}"));
            Assert.Throws<DeliveryDomainException>(() => NoticeSettingsRules.NormalizeTemplate(new string('a', 501)));
        }

        [Fact]
        public void Default_templates_only_use_known_variables()
        {
            Assert.NotNull(NoticeSettingsRules.NormalizeTemplate(NoticeSettings.DefaultDispatchTemplate));
            Assert.NotNull(NoticeSettingsRules.NormalizeTemplate(NoticeSettings.DefaultArrivingTemplate));
        }
    }

    public class RastreioTokenAndPublicViewTests
    {
        [Fact]
        public void Tokens_are_opaque_url_safe_and_unique()
        {
            var a = RastreioToken.Generate();
            var b = RastreioToken.Generate();

            Assert.True(RastreioToken.IsWellFormed(a));
            Assert.NotEqual(a, b);
            Assert.Equal(43, a.Length);
            Assert.DoesNotContain('+', a);
            Assert.DoesNotContain('/', a);
            Assert.False(RastreioToken.IsWellFormed("curto"));
            Assert.False(RastreioToken.IsWellFormed(null));
            Assert.False(RastreioToken.IsWellFormed(new string('a', 42) + "!"));
        }

        [Theory]
        [InlineData(2, "em_rota", false)]
        [InlineData(3, "concluido", true)]
        [InlineData(4, "cancelado", true)]
        [InlineData(1, "em_preparo", false)]
        public void Status_mapping(int status, string key, bool final)
        {
            var (mapped, _, isFinal) = PublicTrackingRules.StatusOf(status);

            Assert.Equal(key, mapped);
            Assert.Equal(final, isFinal);
        }

        [Fact]
        public void Motoboy_position_needs_authorization_route_and_fresh_position()
        {
            Assert.NotNull(PublicTrackingRules.MotoboyPosition(true, 2, -18.9, -48.2, 10));
            Assert.Null(PublicTrackingRules.MotoboyPosition(false, 2, -18.9, -48.2, 10));   // nao autorizou
            Assert.Null(PublicTrackingRules.MotoboyPosition(true, 1, -18.9, -48.2, 10));    // ainda nao saiu
            Assert.Null(PublicTrackingRules.MotoboyPosition(true, 3, -18.9, -48.2, 10));    // concluido
            Assert.Null(PublicTrackingRules.MotoboyPosition(true, 2, -18.9, -48.2, 900));   // posicao velha
            Assert.Null(PublicTrackingRules.MotoboyPosition(true, 2, null, -48.2, 10));
        }

        [Fact]
        public void Destination_is_rounded_to_about_a_hundred_meters()
        {
            var rounded = PublicTrackingRules.Round(-18.918612, -48.277234)!;

            Assert.Equal(new[] { -48.277, -18.919 }, rounded);
            Assert.Null(PublicTrackingRules.Round(null, -48.2));
        }
    }

    public class TrackingNoticeServiceTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        private readonly Mock<IRastreioRepository> _rastreio = new();
        private readonly Mock<IAtendimentoRepository> _atendimento = new();
        private readonly Mock<ITrackingNoticeSender> _sender = new();
        private readonly List<(long Id, string Status, string? Motivo)> _marks = new();
        private long _nextId = 1;

        private TrackingNoticeService Service(bool withBaseUrl = true, string? baseUrl = null)
        {
            var settings = new Dictionary<string, string?>();
            if (withBaseUrl) settings["Rastreio:PublicBaseUrl"] = baseUrl ?? "https://painel.example.com/";
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            _rastreio.Setup(r => r.TryReserveAsync(It.IsAny<int>(), It.IsAny<string>())).ReturnsAsync(() => _nextId++);
            _rastreio.Setup(r => r.MarkAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>()))
                .Callback<long, string, string?, Guid?>((id, status, motivo, _) => _marks.Add((id, status, motivo)))
                .Returns(Task.CompletedTask);
            _rastreio.Setup(r => r.EnsureTokenAsync(It.IsAny<int>())).ReturnsAsync("TOKEN");
            _atendimento.Setup(a => a.GetConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ReturnsAsync(new ConversaDoPedidoDto { ConversaId = Guid.NewGuid(), JanelaAberta = true });
            _sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync(Guid.NewGuid());
            return new TrackingNoticeService(_rastreio.Object, _atendimento.Object, _sender.Object, config, NullLogger<TrackingNoticeService>.Instance);
        }

        private static NoticeCandidate Candidate(Action<NoticeCandidate>? tweak = null)
        {
            var candidate = new NoticeCandidate
            {
                PedidoId = 42, EstabelecimentoId = Guid.NewGuid(), NomeCliente = "Maria Souza", Loja = "Sabor",
                MotoboyId = 7, MotoboyNome = "Diego Santos", MotoboyShares = true,
                DistanceMeters = 5000, LocationAgeSeconds = 5, SpeedMps = 8
            };
            tweak?.Invoke(candidate);
            return candidate;
        }

        private void Candidates(params NoticeCandidate[] items) =>
            _rastreio.Setup(r => r.GetCandidatesAsync()).ReturnsAsync(items);

        [Fact]
        public async Task Dispatch_notice_is_sent_once_with_rendered_text_and_link()
        {
            var service = Service();
            Candidates(Candidate());

            var sent = await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Equal(1, sent);
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.Is<string>(text => text.Contains("Maria") && text.Contains("42") && text.Contains("Diego") && text.Contains("https://painel.example.com/rastreio/TOKEN") && !text.Contains("{"))), Times.Once);
            Assert.Contains(_marks, m => m.Status == "enviada");
        }

        [Fact]
        public async Task Already_done_dispatch_is_not_sent_again()
        {
            var service = Service();
            Candidates(Candidate(c => c.DispatchDone = true));

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            _rastreio.Verify(r => r.TryReserveAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Lost_reservation_means_someone_else_sends_it()
        {
            var service = Service();
            _rastreio.Setup(r => r.TryReserveAsync(It.IsAny<int>(), It.IsAny<string>())).ReturnsAsync((long?)null);
            Candidates(Candidate());

            var sent = await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Equal(0, sent);
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Disabled_dispatch_is_recorded_as_ignored_not_sent()
        {
            var service = Service();
            Candidates(Candidate(c => c.Settings = new NoticeSettings { DispatchEnabled = false, ArrivingEnabled = false }));

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Contains(_marks, m => m.Status == "ignorada" && m.Motivo == "aviso_desligado");
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Outside_24h_window_fails_visibly_without_sending()
        {
            var service = Service();
            _atendimento.Setup(a => a.GetConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ReturnsAsync(new ConversaDoPedidoDto { ConversaId = Guid.NewGuid(), JanelaAberta = false });
            Candidates(Candidate());

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Contains(_marks, m => m.Status == "falhou" && m.Motivo == "fora_da_janela_sem_template");
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Missing_conversation_is_ignored()
        {
            var service = Service();
            _atendimento.Setup(a => a.GetConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(new ConversaDoPedidoDto());
            Candidates(Candidate());

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Contains(_marks, m => m.Status == "ignorada" && m.Motivo == "sem_conversa");
        }

        [Fact]
        public async Task Send_failure_is_recorded_once_and_never_retried_in_the_same_pass()
        {
            var service = Service();
            _sender.Setup(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("boom"));
            Candidates(Candidate());

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
            Assert.Contains(_marks, m => m.Status == "falhou" && m.Motivo!.Contains("boom"));
        }

        [Fact]
        public async Task Without_public_base_url_the_link_variable_has_no_value_and_the_notice_fails_visibly()
        {
            var service = Service(withBaseUrl: false);
            Candidates(Candidate());

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Contains(_marks, m => m.Status == "falhou" && m.Motivo!.StartsWith("variavel_sem_valor"));
            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Placeholder_base_url_counts_as_not_configured()
        {
            var service = Service(baseUrl: "__SET_IN_ENV__");
            Candidates(Candidate());

            await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Arriving_notice_waits_for_sustained_qualification_then_sends_once()
        {
            var service = Service();
            var hysteresis = new ArrivingHysteresis(TimeSpan.FromSeconds(20));
            Candidates(Candidate(c => { c.DispatchDone = true; c.DistanceMeters = 800; }));

            Assert.Equal(0, await service.RunOnceAsync(hysteresis, Now));
            Assert.Equal(0, await service.RunOnceAsync(hysteresis, Now.AddSeconds(10)));
            Assert.Equal(1, await service.RunOnceAsync(hysteresis, Now.AddSeconds(20)));

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.Is<string>(t => t.Contains("chegando") || t.Contains("min"))), Times.Once);
        }

        [Fact]
        public async Task Arriving_notice_never_sent_when_motoboy_did_not_authorize()
        {
            var service = Service();
            var hysteresis = new ArrivingHysteresis(TimeSpan.Zero);
            Candidates(Candidate(c => { c.DispatchDone = true; c.DistanceMeters = 100; c.MotoboyShares = false; }));

            await service.RunOnceAsync(hysteresis, Now);
            await service.RunOnceAsync(hysteresis, Now.AddMinutes(5));

            _sender.Verify(s => s.SendAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            _rastreio.Verify(r => r.TryReserveAsync(It.IsAny<int>(), NoticeTypes.Arriving), Times.Never);
        }

        [Fact]
        public async Task One_failing_order_does_not_block_the_others()
        {
            var service = Service();
            _atendimento.SetupSequence(a => a.GetConversaDoPedidoAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("banco"))
                .ReturnsAsync(new ConversaDoPedidoDto { ConversaId = Guid.NewGuid(), JanelaAberta = true });
            Candidates(Candidate(c => c.PedidoId = 1), Candidate(c => c.PedidoId = 2));

            var sent = await service.RunOnceAsync(new ArrivingHysteresis(), Now);

            Assert.Equal(1, sent);
        }

        [Fact]
        public async Task Public_view_hides_position_without_authorization_and_finished_links_show_nothing()
        {
            var service = Service();
            var token = RastreioToken.Generate();
            _rastreio.Setup(r => r.GetPublicSourceAsync(token)).ReturnsAsync(new PublicTrackingSource
            {
                PedidoId = 42, StatusPedido = 2, Loja = "Sabor", MotoboyNome = "Diego Santos", MotoboyShares = false,
                MotoLat = -18.9, MotoLon = -48.2, LocationAgeSeconds = 5, DestLat = -18.91861, DestLon = -48.27723
            });

            var view = await service.GetPublicViewAsync(token);

            Assert.Null(view.PosicaoMotoboy);
            Assert.Equal("Diego", view.MotoboyNome);
            Assert.Equal(new[] { -48.277, -18.919 }, view.Destino);

            var finished = RastreioToken.Generate();
            _rastreio.Setup(r => r.GetPublicSourceAsync(finished)).ReturnsAsync(new PublicTrackingSource { PedidoId = 42, StatusPedido = 3, Loja = "Sabor", DestLat = -18.9, DestLon = -48.2 });
            var done = await service.GetPublicViewAsync(finished);

            Assert.True(done.Finalizado);
            Assert.Null(done.Destino);
            Assert.Null(done.MotoboyNome);
            _rastreio.Verify(r => r.InvalidateTokenAsync(42), Times.Once);
        }

        [Fact]
        public async Task Public_view_rejects_bad_expired_or_unknown_tokens()
        {
            var service = Service();
            var expired = RastreioToken.Generate();
            _rastreio.Setup(r => r.GetPublicSourceAsync(expired)).ReturnsAsync(new PublicTrackingSource { PedidoId = 1, StatusPedido = 2, Expired = true });

            Assert.Equal(404, (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.GetPublicViewAsync("x"))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.GetPublicViewAsync(RastreioToken.Generate()))).StatusCode);
            Assert.Equal(410, (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.GetPublicViewAsync(expired))).StatusCode);
        }

        [Fact]
        public void First_name_helper()
        {
            Assert.Equal("Maria", TrackingNoticeService.FirstName(" Maria Souza "));
            Assert.Equal("Diego", TrackingNoticeService.FirstName("Diego"));
            Assert.Null(TrackingNoticeService.FirstName("  "));
        }
    }

    public class AvisosMigrationTests
    {
        private static string Read(string name)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "APIBack.csproj"))) dir = Path.GetDirectoryName(dir);
            return File.ReadAllText(Path.Combine(dir ?? string.Empty, "Migrations", "Delivery", name));
        }

        [Fact]
        public void Migration_enforces_one_notice_per_type_and_order_and_safe_defaults()
        {
            var sql = Read("20260928_01_avisos_rastreio.sql");

            Assert.Contains("UNIQUE (pedido_id, tipo)", sql);
            Assert.Contains("rastreio_opt_in BOOLEAN NOT NULL DEFAULT FALSE", sql);
            Assert.Contains("compartilhar_localizacao_cliente BOOLEAN NOT NULL DEFAULT FALSE", sql);
            Assert.Contains("notify_arriving_minutes INTEGER NOT NULL DEFAULT 5", sql);
            Assert.Contains("notify_arriving_radius_m INTEGER NOT NULL DEFAULT 400", sql);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
