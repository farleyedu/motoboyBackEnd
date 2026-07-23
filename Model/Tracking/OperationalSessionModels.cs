using System;

namespace APIBack.Model.Tracking
{
    public sealed class OperationalMotoboyIdentity
    {
        public int MotoboyId { get; set; }
        public int? UsuarioId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public int Status { get; set; }
        public bool IsSimulated { get; set; }
        public bool SimulatorEnabled { get; set; }
    }

    public sealed class OperationalSessionRecord
    {
        public Guid SessionId { get; set; }
        public long SessionEpoch { get; set; }
        public int MotoboyId { get; set; }
        public int? UsuarioId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Origin { get; set; } = "mobile";
        public string? ClientInstanceId { get; set; }
        public int? StartedByUserId { get; set; }
        public Guid? IdempotencyKey { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset LastHeartbeatAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public string? EndReason { get; set; }
        public long Version { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public int Status { get; set; }
    }

    public sealed class OperationalLocationWrite
    {
        public Guid SampleId { get; set; }
        public long Sequence { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public DateTimeOffset CapturedAtUtc { get; set; }
        public string PayloadHash { get; set; } = string.Empty;
    }

    public sealed class OperationalLocationWriteResult
    {
        public string Outcome { get; set; } = "accepted";
        public bool UpdatedCurrent { get; set; }
        public long SessionVersion { get; set; }
        public DateTimeOffset ReceivedAtUtc { get; set; }
    }

    public sealed class DeliveryOutboxRecord
    {
        public Guid EventId { get; set; }
        public string EventName { get; set; } = string.Empty;
        public string TargetGroup { get; set; } = string.Empty;
        public string Payload { get; set; } = "{}";
    }
}

