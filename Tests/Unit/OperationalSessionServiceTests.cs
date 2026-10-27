using System;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Auth;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class OperationalSessionServiceTests
    {
        private readonly Mock<IOperationalSessionRepository> _repository = new();
        private readonly Mock<IJwtService> _jwtService = new();
        private readonly DeliveryTrackingOptions _options = new()
        {
            Enabled = true,
            SimulatorEnabled = true,
            PresenceTtlSeconds = 720,
            HeartbeatIntervalSeconds = 240,
            LocationFreshnessSeconds = 120,
            OperationalTokenExpirationMinutes = 60
        };

        [Fact]
        public async Task AutoStart_ReturnsOperationalTokenAndSeparatePresenceExpiry()
        {
            var session = CreateSession("simulator");
            JwtPayload? capturedPayload = null;
            _repository.Setup(r => r.StartSimulatorSessionAsync(7, session.EstabelecimentoId, It.IsAny<Guid>(), null))
                .ReturnsAsync(session);
            _jwtService.Setup(j => j.GenerateToken(It.IsAny<JwtPayload>(), It.IsAny<TimeSpan>()))
                .Callback<JwtPayload, TimeSpan>((payload, _) => capturedPayload = payload)
                .Returns("operational-token");
            var service = CreateService();

            var result = await service.AutoStartSimulatorSessionAsync(
                7,
                session.EstabelecimentoId,
                new StartSimulatorSessionRequest { AttemptId = Guid.NewGuid() });

            Assert.Equal("operational-token", result.AccessToken);
            Assert.Equal(3600, result.ExpiresIn);
            Assert.Equal(session.ExpiresAtUtc, result.Session.PresenceExpiresAtUtc);
            Assert.Equal(240, result.Session.HeartbeatIntervalSeconds);
            Assert.NotNull(capturedPayload);
            Assert.Equal("delivery_operational", capturedPayload!.TokenUse);
            Assert.Equal(session.SessionEpoch, capturedPayload.SessionEpoch);
            Assert.Equal(session.SessionId, capturedPayload.MotoboySessionId);
            Assert.Equal(7, capturedPayload.UserId);
            Assert.Equal(session.NextLocationSequence, result.NextLocationSequence);
        }

        [Fact]
        public async Task EndSession_PropagatesPendingWorkConflict()
        {
            var session = CreateSession("simulator");
            var payload = new JwtPayload
            {
                TokenUse = "delivery_operational",
                MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId,
                SessionEpoch = session.SessionEpoch
            };
            _repository
                .Setup(r => r.EndSessionAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, "client_end", false))
                .ThrowsAsync(new DeliveryDomainException(
                    409,
                    "MOTOBOY_HAS_PENDING_WORK",
                    "O motoboy possui pendencias."));
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.EndSessionAsync(payload, "client_end"));

            Assert.Equal(409, exception.StatusCode);
            Assert.Equal("MOTOBOY_HAS_PENDING_WORK", exception.Code);
        }

        [Fact]
        public async Task Heartbeat_RotatesTokenForSameSession()
        {
            var session = CreateSession("mobile");
            _repository.Setup(r => r.HeartbeatAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, default))
                .ReturnsAsync(session);
            _jwtService.Setup(j => j.GenerateToken(It.IsAny<JwtPayload>(), It.IsAny<TimeSpan>()))
                .Returns("renewed-token");
            var service = CreateService();

            var response = await service.HeartbeatAsync(new JwtPayload
            {
                TokenUse = "delivery_operational",
                MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId,
                SessionEpoch = session.SessionEpoch
            });

            Assert.Equal("renewed-token", response.AccessToken);
            Assert.Equal(session.SessionId, response.Session.SessionId);
            Assert.Equal(3600, response.ExpiresIn);
            Assert.Equal(240, response.Session.HeartbeatIntervalSeconds);
            _repository.Verify(r => r.HeartbeatAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, default), Times.Once);
        }

        [Fact]
        public async Task Heartbeat_ReportsConnectionAndInactivityDeadlinesSeparately()
        {
            var session = CreateSession("mobile");
            var now = DateTimeOffset.UtcNow;
            session.LastHeartbeatAtUtc = now;
            session.LastSeenAtUtc = now.AddSeconds(-30);
            session.LastHumanActivityAtUtc = now.AddHours(-2);
            session.ExpiresAtUtc = now.AddHours(5);
            _repository.Setup(r => r.HeartbeatAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, default)).ReturnsAsync(session);
            _jwtService.Setup(j => j.GenerateToken(It.IsAny<JwtPayload>(), It.IsAny<TimeSpan>())).Returns("t");

            var response = await CreateService().HeartbeatAsync(new JwtPayload
            {
                TokenUse = "delivery_operational", MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId, SessionEpoch = session.SessionEpoch
            });

            Assert.Equal(now.AddSeconds(_options.PresenceTtlSeconds), response.Session.PresenceExpiresAtUtc);
            Assert.Equal(session.LastHumanActivityAtUtc, response.Session.LastHumanActivityAtUtc);
            Assert.Equal(session.ExpiresAtUtc, response.Session.InactivityExpiresAtUtc);
            Assert.InRange(response.Session.InactivityRemainingSeconds, 5 * 3600 - 5, 5 * 3600);
        }

        [Fact]
        public async Task Activity_ReturnsRemainingTimeFromTheServerClock()
        {
            var session = CreateSession("mobile");
            var now = DateTimeOffset.UtcNow;
            _repository.Setup(r => r.RecordActivityAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, default))
                .ReturnsAsync(new OperationalActivityResult { Recorded = true, ServerTimeUtc = now, LastHumanActivityAtUtc = now, ExpiresAtUtc = now.AddHours(7) });

            var response = await CreateService().RecordActivityAsync(new JwtPayload
            {
                TokenUse = "delivery_operational", MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId, SessionEpoch = session.SessionEpoch
            });

            Assert.True(response.Recorded);
            Assert.Equal(7 * 3600, response.InactivityRemainingSeconds);
        }

        [Fact]
        public async Task EndSession_ForwardsTheExplicitConfirmationToTheRepository()
        {
            var session = CreateSession("mobile");
            _repository.Setup(r => r.EndSessionAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, "client_end", true))
                .ReturnsAsync(session);

            await CreateService().EndSessionAsync(new JwtPayload
            {
                TokenUse = "delivery_operational", MotoboySessionId = session.SessionId,
                MotoboyId = session.MotoboyId, SessionEpoch = session.SessionEpoch
            }, "client_end", confirmPendingWork: true);

            _repository.Verify(r => r.EndSessionAsync(session.SessionId, session.MotoboyId, session.SessionEpoch, "client_end", true), Times.Once);
        }

        [Fact]
        public async Task DisabledTracking_DoesNotCallRepository()
        {
            _options.Enabled = false;
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AutoStartSimulatorSessionAsync(
                    7,
                    Guid.NewGuid(),
                    new StartSimulatorSessionRequest { AttemptId = Guid.NewGuid() }));

            Assert.Equal("DELIVERY_TRACKING_DISABLED", exception.Code);
            _repository.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task AutoStart_PassesChosenMotoboyToRepository()
        {
            var session = CreateSession("simulator");
            _repository.Setup(r => r.StartSimulatorSessionAsync(7, session.EstabelecimentoId, It.IsAny<Guid>(), 42))
                .ReturnsAsync(session);
            _jwtService.Setup(j => j.GenerateToken(It.IsAny<JwtPayload>(), It.IsAny<TimeSpan>())).Returns("token");
            var service = CreateService();

            await service.AutoStartSimulatorSessionAsync(
                7,
                session.EstabelecimentoId,
                new StartSimulatorSessionRequest { AttemptId = Guid.NewGuid(), MotoboyId = 42 });

            _repository.Verify(r => r.StartSimulatorSessionAsync(7, session.EstabelecimentoId, It.IsAny<Guid>(), 42), Times.Once);
        }

        [Fact]
        public async Task AutoStart_RejectsInvalidChosenMotoboy()
        {
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.AutoStartSimulatorSessionAsync(
                    7,
                    Guid.NewGuid(),
                    new StartSimulatorSessionRequest { AttemptId = Guid.NewGuid(), MotoboyId = 0 }));

            Assert.Equal("INVALID_REQUEST", exception.Code);
        }

        [Fact]
        public async Task CreateSimulatorMotoboy_OtherEstablishmentRequiresAccess()
        {
            var active = Guid.NewGuid();
            var other = Guid.NewGuid();
            _repository.Setup(r => r.CanUserManageEstablishmentAsync(7, false, other)).ReturnsAsync(false);
            var service = CreateService();

            var exception = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
                service.CreateSimulatorMotoboyAsync(7, false, active,
                    new CreateSimulatorMotoboyRequest { Nome = "Teste", EstabelecimentoId = other }));

            Assert.Equal("ESTABLISHMENT_FORBIDDEN", exception.Code);
            _repository.Verify(r => r.CreateSimulatorMotoboyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task CreateSimulatorMotoboy_UsesChosenEstablishmentWhenAllowed()
        {
            var active = Guid.NewGuid();
            var other = Guid.NewGuid();
            _repository.Setup(r => r.CanUserManageEstablishmentAsync(7, true, other)).ReturnsAsync(true);
            _repository.Setup(r => r.CreateSimulatorMotoboyAsync(other, "Teste", null))
                .ReturnsAsync(new OperationalMotoboyIdentity { MotoboyId = 9, EstabelecimentoId = other, Nome = "Teste" });
            var service = CreateService();

            var result = await service.CreateSimulatorMotoboyAsync(7, true, active,
                new CreateSimulatorMotoboyRequest { Nome = "Teste", EstabelecimentoId = other });

            Assert.Equal(9, result.Id);
            _repository.Verify(r => r.CreateSimulatorMotoboyAsync(other, "Teste", null), Times.Once);
        }

        [Fact]
        public async Task CreateSimulatorMotoboy_DefaultsToActiveEstablishmentWithoutAccessCheck()
        {
            var active = Guid.NewGuid();
            _repository.Setup(r => r.CreateSimulatorMotoboyAsync(active, "Teste", null))
                .ReturnsAsync(new OperationalMotoboyIdentity { MotoboyId = 3, EstabelecimentoId = active, Nome = "Teste" });
            var service = CreateService();

            await service.CreateSimulatorMotoboyAsync(7, false, active, new CreateSimulatorMotoboyRequest { Nome = "Teste" });

            _repository.Verify(r => r.CanUserManageEstablishmentAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<Guid>()), Times.Never);
        }

        [Theory]
        [InlineData("web", 3600)]
        [InlineData("native", 3600)]
        public async Task Start_UsesClientPlatformForSessionAndTokenLifetime(string platform, int expectedSeconds)
        {
            var session = CreateSession("mobile");
            session.DeviceType = platform == "web" ? "web" : "mobile";
            var attempt = Guid.NewGuid();
            _repository.Setup(r => r.StartMobileSessionAsync(7, session.EstabelecimentoId, attempt, "qa", null, false, platform, null))
                .ReturnsAsync(session);
            TimeSpan lifetime = default;
            _jwtService.Setup(j => j.GenerateToken(It.IsAny<JwtPayload>(), It.IsAny<TimeSpan>()))
                .Callback<JwtPayload, TimeSpan>((_, duration) => lifetime = duration).Returns("token-qa");
            var result = await CreateService().StartMobileSessionAsync(7, session.EstabelecimentoId,
                new StartOperationalSessionRequest { AttemptId = attempt, ClientInstanceId = "qa", ClientPlatform = platform });
            Assert.Equal(expectedSeconds, result.ExpiresIn);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), lifetime);
            Assert.Equal(platform, result.Session.ClientPlatform);
            Assert.Equal("mobile", result.Session.Origin);
        }

        [Fact]
        public async Task Start_RejectsUnknownPlatformBeforeCreatingSession()
        {
            var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => CreateService().StartMobileSessionAsync(7, Guid.NewGuid(),
                new StartOperationalSessionRequest { AttemptId = Guid.NewGuid(), ClientPlatform = "simulator" }));
            Assert.Equal("INVALID_CLIENT_PLATFORM", error.Code);
            _repository.VerifyNoOtherCalls();
        }

        private OperationalSessionService CreateService() => new(
            _repository.Object,
            _jwtService.Object,
            Microsoft.Extensions.Options.Options.Create(_options),
            new Mock<APIBack.Repository.Interface.IPedidoQueueRepository>().Object);

        private static OperationalSessionRecord CreateSession(string origin)
        {
            var now = DateTimeOffset.UtcNow;
            return new OperationalSessionRecord
            {
                SessionId = Guid.NewGuid(),
                SessionEpoch = 42,
                MotoboyId = 123,
                UsuarioId = origin == "mobile" ? 7 : null,
                EstabelecimentoId = Guid.NewGuid(),
                Origin = origin,
                StartedByUserId = 7,
                StartedAtUtc = now,
                LastHeartbeatAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(720),
                Version = 1,
                Nome = "Motoboy Teste",
                Status = 2,
                NextLocationSequence = 37
            };
        }
    }
}
