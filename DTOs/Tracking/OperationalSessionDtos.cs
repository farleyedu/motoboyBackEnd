using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Tracking
{
    public class StartOperationalSessionRequest
    {
        public Guid AttemptId { get; set; }
        public string? ClientInstanceId { get; set; }
    }

    public sealed class SwitchOperationalSessionRequest : StartOperationalSessionRequest
    {
        public Guid ExpectedSessionId { get; set; }
        public Guid TargetEstablishmentId { get; set; }
        public bool Confirm { get; set; }
    }

    public sealed class StartSimulatorSessionRequest
    {
        public Guid AttemptId { get; set; }
    }

    public sealed class OperationalSessionDto
    {
        public Guid SessionId { get; set; }
        public long Epoch { get; set; }
        public string Origin { get; set; } = "mobile";
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset PresenceExpiresAtUtc { get; set; }
        public int HeartbeatIntervalSeconds { get; set; }
        public long Version { get; set; }
        public bool IsEnded { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public string? EndReason { get; set; }
    }

    public class OperationalSessionTokenResponse
    {
        public Guid EstablishmentId { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public MotoboyMapDto Motoboy { get; set; } = new();
        public OperationalSessionDto Session { get; set; } = new();
    }

    public sealed class SimulatorAutoStartResponse : OperationalSessionTokenResponse
    {
    }

    public sealed class OperationalSessionConflictDto
    {
        public string Code { get; set; } = "SESSION_CONFLICT";
        public Guid SessionId { get; set; }
        public Guid EstablishmentId { get; set; }
        public string Origin { get; set; } = string.Empty;
        public DateTimeOffset PresenceExpiresAtUtc { get; set; }
    }

    public sealed class OperationalHeartbeatResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public DateTimeOffset ServerTimeUtc { get; set; }
        public OperationalSessionDto Session { get; set; } = new();
    }

    public sealed class OperationalLocationRequest
    {
        public Guid? SampleId { get; set; }
        public long Sequence { get; set; }
        public DateTimeOffset? CapturedAtUtc { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
    }

    public sealed class OperationalLocationAckDto
    {
        public Guid SampleId { get; set; }
        public long Sequence { get; set; }
        public string Outcome { get; set; } = "accepted";
        public bool UpdatedCurrent { get; set; }
        public long SessionVersion { get; set; }
        public DateTimeOffset ReceivedAtUtc { get; set; }
    }

    public sealed class SimulatorCandidateDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public bool SimulatorEnabled { get; set; }
        public bool Eligible { get; set; }
        public string? UnavailableReason { get; set; }
    }

    public sealed class SimulatorCandidatesResponse
    {
        public Guid EstablishmentId { get; set; }
        public IReadOnlyCollection<SimulatorCandidateDto> Candidates { get; set; } = Array.Empty<SimulatorCandidateDto>();
    }

    public sealed class DeliveryTrackingSnapshotDto
    {
        public Guid EstablishmentId { get; set; }
        public DateTimeOffset ServerTimeUtc { get; set; }
        public IReadOnlyCollection<OnlineMotoboyDto> Motoboys { get; set; } = Array.Empty<OnlineMotoboyDto>();
    }

    public sealed class OnlineMotoboyDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public string Status { get; set; } = "online";
        public Guid SessionId { get; set; }
        public long SessionEpoch { get; set; }
        public long Version { get; set; }
        public string Origin { get; set; } = string.Empty;
        public DateTimeOffset PresenceExpiresAtUtc { get; set; }
        public bool HasRecentLocation { get; set; }
        public OperationalLocationDto? Location { get; set; }
    }

    public sealed class OperationalLocationDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double[] Coordinates => new[] { Longitude, Latitude };
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public long Sequence { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public DateTimeOffset ReceivedAtUtc { get; set; }
    }
}
