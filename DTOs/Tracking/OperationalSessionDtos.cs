using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Tracking
{
    public class StartOperationalSessionRequest
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Guid? LoginSessionId { get; set; }
        public Guid AttemptId { get; set; }
        public string? ClientInstanceId { get; set; }
        public string? ClientPlatform { get; set; }
        public Guid? ResumeSessionId { get; set; }
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
        /// <summary>Motoboy simulado escolhido. Vazio = o primeiro elegivel (comportamento anterior).</summary>
        public int? MotoboyId { get; set; }
    }

    public sealed class OperationalSessionDto
    {
        public Guid SessionId { get; set; }
        public long Epoch { get; set; }
        public string Origin { get; set; } = "mobile";
        public string ClientPlatform { get; set; } = "native";
        public DateTimeOffset StartedAtUtc { get; set; }
        /// <summary>Fim da janela de conexao (ultimo contato + 12 min). Nao encerra o turno.</summary>
        public DateTimeOffset PresenceExpiresAtUtc { get; set; }
        public DateTimeOffset? LastContactAtUtc { get; set; }
        public DateTimeOffset? LastHumanActivityAtUtc { get; set; }
        /// <summary>Quando o turno sera encerrado por inatividade se nenhuma acao humana ocorrer.</summary>
        public DateTimeOffset InactivityExpiresAtUtc { get; set; }
        public int InactivityRemainingSeconds { get; set; }
        public int HeartbeatIntervalSeconds { get; set; }
        public long Version { get; set; }
        public long NextLocationSequence { get; set; } = 1;
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
        public long NextLocationSequence { get; set; } = 1;
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

    /// <summary>Resposta de POST /session/activity.</summary>
    public sealed class OperationalActivityResponse
    {
        /// <summary>false quando o limite de frequencia ignorou o toque (a ultima atividade ja e recente).</summary>
        public bool Recorded { get; set; }
        public DateTimeOffset ServerTimeUtc { get; set; }
        public DateTimeOffset LastHumanActivityAtUtc { get; set; }
        public DateTimeOffset InactivityExpiresAtUtc { get; set; }
        public int InactivityRemainingSeconds { get; set; }
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
        public string? Code { get; set; }
    }

    public sealed class OperationalLocationBatchRequest
    {
        public IReadOnlyList<OperationalLocationRequest>? Samples { get; set; }
    }

    public sealed class OperationalLocationBatchAckDto
    {
        public IReadOnlyList<OperationalLocationAckDto> Samples { get; set; } = Array.Empty<OperationalLocationAckDto>();
    }

    public sealed class SimulatorCandidateDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public string? Telefone { get; set; }
        public string? PlacaMoto { get; set; }
        public string? MarcaMoto { get; set; }
        public string? ModeloMoto { get; set; }
        public bool SimulatorEnabled { get; set; }
        public bool Eligible { get; set; }
        public string? UnavailableReason { get; set; }
        /// <summary>Ja ha uma sessao de simulador aberta: ao escolher o motoboy, ela e assumida.</summary>
        public bool TakesOverSession { get; set; }
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
        /// <summary>"native" | "web" | "simulator".</summary>
        public string ClientPlatform { get; set; } = "native";
        public DateTimeOffset ShiftStartedAtUtc { get; set; }
        public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
        public DateTimeOffset? LastContactAtUtc { get; set; }
        public DateTimeOffset? LastHumanActivityAtUtc { get; set; }
        /// <summary>"connected" (contato nos ultimos 12 min) | "disconnected". O turno continua ativo nos dois.</summary>
        public string PresenceStatus { get; set; } = "connected";
        /// <summary>"enabled" | "unavailable" | "unknown".</summary>
        public string PushCapability { get; set; } = "unknown";
        public DateTimeOffset InactivityDeadlineAtUtc { get; set; }
        public int InactivityRemainingSeconds { get; set; }
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
