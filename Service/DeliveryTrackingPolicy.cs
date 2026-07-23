using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;
using APIBack.Options;

namespace APIBack.Service
{
    public static class DeliveryTrackingPolicy
    {
        public static bool IsOnline(OperationalSessionRecord session, DateTimeOffset serverNow) =>
            !session.EndedAtUtc.HasValue && session.ExpiresAtUtc > serverNow;

        public static bool IsLocationFresh(DateTimeOffset receivedAtUtc, DateTimeOffset serverNow, int freshnessSeconds) =>
            receivedAtUtc <= serverNow && receivedAtUtc >= serverNow.AddSeconds(-freshnessSeconds);

        public static OperationalLocationWrite ValidateAndMap(
            OperationalLocationRequest request,
            DeliveryTrackingOptions options,
            DateTimeOffset serverNow)
        {
            if (!request.SampleId.HasValue || request.SampleId == Guid.Empty)
            {
                throw Invalid("sampleId e obrigatorio.");
            }

            if (request.Sequence <= 0)
            {
                throw Invalid("sequence deve ser maior que zero.");
            }

            if (!request.CapturedAtUtc.HasValue)
            {
                throw Invalid("capturedAtUtc e obrigatorio.");
            }

            if (!request.Latitude.HasValue || !double.IsFinite(request.Latitude.Value) || request.Latitude is < -90 or > 90)
            {
                throw Invalid("latitude invalida.");
            }

            if (!request.Longitude.HasValue || !double.IsFinite(request.Longitude.Value) || request.Longitude is < -180 or > 180)
            {
                throw Invalid("longitude invalida.");
            }

            if (request.AccuracyMeters.HasValue &&
                (!double.IsFinite(request.AccuracyMeters.Value) || request.AccuracyMeters is < 0 || request.AccuracyMeters > 2000))
            {
                throw Invalid("accuracyMeters invalido.");
            }

            if (request.AccuracyMeters > options.MaxAccuracyMeters)
            {
                throw Invalid("accuracyMeters excede o limite configurado.");
            }

            if (request.SpeedMps.HasValue &&
                (!double.IsFinite(request.SpeedMps.Value) || request.SpeedMps is < 0 || request.SpeedMps > 100))
            {
                throw Invalid("speedMps invalido.");
            }

            if (request.SpeedMps > options.MaxSpeedMetersPerSecond)
            {
                throw Invalid("speedMps excede o limite configurado.");
            }

            if (request.HeadingDegrees.HasValue &&
                (!double.IsFinite(request.HeadingDegrees.Value) || request.HeadingDegrees is < 0 or >= 360))
            {
                throw Invalid("headingDegrees invalido.");
            }

            var capturedAtUtc = request.CapturedAtUtc.Value.ToUniversalTime();
            if (capturedAtUtc > serverNow.AddSeconds(options.MaxCapturedAtFutureSeconds) ||
                capturedAtUtc < serverNow.AddSeconds(-options.MaxCapturedAtPastSeconds))
            {
                throw Invalid("capturedAtUtc fora da janela aceita.");
            }

            var trackingMode = string.Equals(request.TrackingMode, "active_route", StringComparison.OrdinalIgnoreCase)
                ? "active_route"
                : "online_idle";
            var quality = !request.AccuracyMeters.HasValue
                ? "unknown"
                : request.AccuracyMeters <= 50 ? "good" : request.AccuracyMeters <= 150 ? "fair" : "poor";

            var mapped = new OperationalLocationWrite
            {
                SampleId = request.SampleId.Value,
                Sequence = request.Sequence,
                CapturedAtUtc = capturedAtUtc,
                Latitude = request.Latitude.Value,
                Longitude = request.Longitude.Value,
                AccuracyMeters = request.AccuracyMeters,
                SpeedMps = request.SpeedMps,
                HeadingDegrees = request.HeadingDegrees,
                TrackingMode = trackingMode,
                Quality = quality
            };
            mapped.PayloadHash = ComputePayloadHash(mapped);
            return mapped;
        }

        public static string ComputePayloadHash(OperationalLocationWrite value)
        {
            var canonical = string.Join("|",
                value.SampleId.ToString("D"),
                value.Sequence.ToString(CultureInfo.InvariantCulture),
                value.Latitude.ToString("R", CultureInfo.InvariantCulture),
                value.Longitude.ToString("R", CultureInfo.InvariantCulture),
                value.AccuracyMeters?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                value.SpeedMps?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                value.HeadingDegrees?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                value.TrackingMode,
                value.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private static DeliveryDomainException Invalid(string message) =>
            new(422, "LOCATION_INVALID", message);
    }
}

