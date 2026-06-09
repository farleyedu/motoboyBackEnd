using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Tracking
{
    public class MotoboyStatusRequest
    {
        public string Status { get; set; } = "online";
        public string? TrackingMode { get; set; }
    }

    public class CreateSimulatorMotoboyRequest
    {
        public string Nome { get; set; } = "Motoboy Simulado";
        public string? Telefone { get; set; }
    }

    public class SimulatorMotoboySessionResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public Guid SessionId { get; set; }
        public MotoboyMapDto Motoboy { get; set; } = new();
    }

    public class MotoboyLocationRequest
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public DateTimeOffset? ClientTimestampUtc { get; set; }
        public long? Sequence { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public List<int>? PedidoIds { get; set; }
    }

    public class MotoboyLocationBatchRequest
    {
        public List<MotoboyLocationRequest> Locations { get; set; } = new();
    }

    public class MotoboyLocationResult
    {
        public bool Accepted { get; set; }
        public bool UpdatedCurrentState { get; set; }
        public string? Reason { get; set; }
        public MotoboyRealtimeDto? Location { get; set; }
    }

    public class MotoboyLocationBatchResult
    {
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public List<MotoboyLocationResult> Results { get; set; } = new();
    }

    public class DeliveryMapStateDto
    {
        public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<MotoboyMapDto> Motoboys { get; set; } = new();
        public List<OrderMapDto> Pedidos { get; set; } = new();
    }

    public class MotoboyMapDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Avatar { get; set; }
        public string Status { get; set; } = "offline";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double[]? Location => Longitude.HasValue && Latitude.HasValue
            ? new[] { Longitude.Value, Latitude.Value }
            : null;
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public DateTimeOffset? ClientTimestampUtc { get; set; }
        public DateTimeOffset? ServerReceivedAtUtc { get; set; }
        public List<DeliveryMapItemDto> Pedidos { get; set; } = new();
    }

    public class DeliveryMapItemDto
    {
        public int Id { get; set; }
        public string Status { get; set; } = "pendente";
        public string Address { get; set; } = string.Empty;
        public string Items { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? DepartureTime { get; set; }
        public string? Eta { get; set; }
        public int EtaMinutes { get; set; }
        public double[] Coordinates { get; set; } = Array.Empty<double>();
    }

    public class OrderMapDto
    {
        public int Id { get; set; }
        public string? NomeCliente { get; set; }
        public string? IdIfood { get; set; }
        public string? TelefoneCliente { get; set; }
        public DateTime? DataPedido { get; set; }
        public string? EnderecoEntrega { get; set; }
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? Region { get; set; }
        public string StatusPedido { get; set; } = "pendente";
        public int? AssignedDriver { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double[]? Coordinates => Longitude.HasValue && Latitude.HasValue
            ? new[] { Longitude.Value, Latitude.Value }
            : null;
        public DateTime? HorarioPedido { get; set; }
        public DateTime? PrevisaoEntrega { get; set; }
        public DateTime? HorarioSaida { get; set; }
        public DateTime? HorarioEntrega { get; set; }
    }

    public class MotoboyLocationHistoryPointDto
    {
        public int MotoboyId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double[] Coordinates => new[] { Longitude, Latitude };
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

    public class MotoboyRealtimeDto
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Status { get; set; } = "online";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double[] Location => new[] { Longitude, Latitude };
        public double? AccuracyMeters { get; set; }
        public double? SpeedMps { get; set; }
        public double? HeadingDegrees { get; set; }
        public string TrackingMode { get; set; } = "online_idle";
        public string Quality { get; set; } = "unknown";
        public DateTimeOffset ClientTimestampUtc { get; set; }
        public DateTimeOffset ServerReceivedAtUtc { get; set; }
        public long? Sequence { get; set; }
    }

    public class MotoboyStatusRealtimeDto
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Status { get; set; } = "offline";
        public string TrackingMode { get; set; } = "online_idle";
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public class DeliveryRouteAssignedRealtimeDto
    {
        public int MotoboyId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public List<int> PedidoIds { get; set; } = new();
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
