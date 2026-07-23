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
            PresenceTtlSeconds = 90,
            HeartbeatIntervalSeconds = 25,
            LocationFreshnessSeconds = 120,
            OperationalTokenExpirationMinutes = 60
        };

        [Fact]
        public async Task AutoStart_ReturnsOperationalTokenAndSeparatePresenceExpiry()
        {
            var session = CreateSession("simulator");
            JwtPayload? capturedPayload = null;
            _repository.Setup(r => r.StartSimulatorSessionAsync(7, session.EstabelecimentoId, It.IsAny<Guid>()))
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
            Assert.Equal(25, result.Session.HeartbeatIntervalSeconds);
            Assert.NotNull(capturedPayload);
            Assert.Equal("delivery_operational", capturedPayload!.TokenUse);
            Assert.Equal(session.SessionEpoch, capturedPayload.SessionEpoch);
            Assert.Equal(session.SessionId, capturedPayload.MotoboySessionId);
            Assert.Equal(7, capturedPayload.UserId);
        }

        [Fact]
        public async Task Heartbeat_RotatesTokenForSameSession()
        {
            var session = CreateSession("mobile");
            _repository.Setup(r => r.HeartbeatAsync(session.SessionId, session.MotoboyId, session.SessionEpoch))
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
            _repository.Verify(r => r.HeartbeatAsync(session.SessionId, session.MotoboyId, session.SessionEpoch), Times.Once);
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

        private OperationalSessionService CreateService() => new(
            _repository.Object,
            _jwtService.Object,
            Microsoft.Extensions.Options.Options.Create(_options));

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
                ExpiresAtUtc = now.AddSeconds(90),
                Version = 1,
                Nome = "Motoboy Teste",
                Status = 2
            };
        }
    }
}
