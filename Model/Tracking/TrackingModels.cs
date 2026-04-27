using System;

namespace APIBack.Model.Tracking
{
    public class MotoboyTrackingIdentity
    {
        public int MotoboyId { get; set; }
        public int? UsuarioId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public int Status { get; set; }
    }

    public class MotoboyLocationState
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public long? LastSequence { get; set; }
        public DateTimeOffset ClientTimestampUtc { get; set; }
        public DateTimeOffset ServerReceivedAtUtc { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class MotoboyLocationHistoryPoint
    {
        public long Id { get; set; }
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public DateTimeOffset ClientTimestampUtc { get; set; }
        public DateTimeOffset ServerReceivedAtUtc { get; set; }
        public DateOnly LocalDate { get; set; }
        public long? Sequence { get; set; }
    }
}
