using System;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DeliveryTrackingPolicyTests
    {
        private readonly DeliveryTrackingOptions _options = new()
        {
            PresenceTtlSeconds = 90,
            HeartbeatIntervalSeconds = 25,
            LocationFreshnessSeconds = 120,
            MaxCapturedAtFutureSeconds = 120,
            MaxCapturedAtPastSeconds = 86400,
            MaxAccuracyMeters = 2000,
            MaxSpeedMetersPerSecond = 100
        };

        [Fact]
        public void ValidateAndMap_ValidSample_ProducesStableHash()
        {
            var now = new DateTimeOffset(2026, 7, 22, 15, 0, 0, TimeSpan.Zero);
            var request = ValidRequest(now);

            var first = DeliveryTrackingPolicy.ValidateAndMap(request, _options, now);
            var second = DeliveryTrackingPolicy.ValidateAndMap(request, _options, now);

            Assert.Equal(first.PayloadHash, second.PayloadHash);
            Assert.Equal("active_route", first.TrackingMode);
            Assert.Equal("good", first.Quality);
        }

        [Fact]
        public void ValidateAndMap_ChangedCoordinates_ChangesHash()
        {
            var now = DateTimeOffset.UtcNow;
            var first = DeliveryTrackingPolicy.ValidateAndMap(ValidRequest(now), _options, now);
            var changed = ValidRequest(now);
            changed.Latitude += 0.001;

            var second = DeliveryTrackingPolicy.ValidateAndMap(changed, _options, now);

            Assert.NotEqual(first.PayloadHash, second.PayloadHash);
        }

        [Theory]
        [InlineData(-91, -46)]
        [InlineData(91, -46)]
        [InlineData(-23, -181)]
        [InlineData(-23, 181)]
        public void ValidateAndMap_InvalidCoordinates_ReturnsLocationInvalid(double latitude, double longitude)
        {
            var now = DateTimeOffset.UtcNow;
            var request = ValidRequest(now);
            request.Latitude = latitude;
            request.Longitude = longitude;

            var exception = Assert.Throws<DeliveryDomainException>(() =>
                DeliveryTrackingPolicy.ValidateAndMap(request, _options, now));

            Assert.Equal("LOCATION_INVALID", exception.Code);
            Assert.Equal(422, exception.StatusCode);
        }

        [Fact]
        public void ValidateAndMap_FutureTimestamp_ReturnsLocationInvalid()
        {
            var now = DateTimeOffset.UtcNow;
            var request = ValidRequest(now);
            request.CapturedAtUtc = now.AddSeconds(_options.MaxCapturedAtFutureSeconds + 1);

            var exception = Assert.Throws<DeliveryDomainException>(() =>
                DeliveryTrackingPolicy.ValidateAndMap(request, _options, now));

            Assert.Equal("LOCATION_INVALID", exception.Code);
        }

        [Fact]
        public void PresenceAndFreshness_UseServerWindows()
        {
            var now = DateTimeOffset.UtcNow;
            var session = new OperationalSessionRecord { ExpiresAtUtc = now.AddSeconds(1) };

            Assert.True(DeliveryTrackingPolicy.IsOnline(session, now));
            Assert.False(DeliveryTrackingPolicy.IsOnline(session, now.AddSeconds(2)));
            Assert.True(DeliveryTrackingPolicy.IsLocationFresh(now.AddSeconds(-120), now, 120));
            Assert.False(DeliveryTrackingPolicy.IsLocationFresh(now.AddSeconds(-121), now, 120));
            Assert.False(DeliveryTrackingPolicy.IsLocationFresh(now.AddSeconds(1), now, 120));
        }

        private static OperationalLocationRequest ValidRequest(DateTimeOffset now) => new()
        {
            SampleId = Guid.NewGuid(),
            Sequence = 1,
            CapturedAtUtc = now,
            Latitude = -23.55052,
            Longitude = -46.633308,
            AccuracyMeters = 20,
            SpeedMps = 8,
            HeadingDegrees = 180,
            TrackingMode = "active_route"
        };
    }
}

