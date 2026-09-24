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
        /// <summary>
        /// Estabelecimento ao qual o motoboy fica vinculado. Vazio = estabelecimento ativo.
        /// Validado contra os vinculos do usuario (super admin: qualquer ativo).
        /// </summary>
        public Guid? EstabelecimentoId { get; set; }
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
        // Posicao cadastrada do restaurante. O painel usa como centro do mapa quando
        // ainda nao ha pedido nem motoboy com coordenada (antes o mapa nem era criado),
        // e o simulador oferece como ponto de partida do motoboy.
        public double? EstabelecimentoLatitude { get; set; }
        public double? EstabelecimentoLongitude { get; set; }
        public string? EstabelecimentoCidade { get; set; }
        public string? EstabelecimentoUf { get; set; }
        public DeliveryDayMetricsDto Metrics { get; set; } = new();
        public List<MotoboyDayStatsDto> MotoboyStats { get; set; } = new();
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

        // Pagamento e entrega (antes o painel mostrava "A confirmar" fixo).
        public string? TipoPagamento { get; set; }
        public string? StatusPagamento { get; set; }
        public decimal? Troco { get; set; }
        public decimal? DistanciaKm { get; set; }
        public string? Observacoes { get; set; }
        public string? EntregaRua { get; set; }
        public string? EntregaNumero { get; set; }
        public string? EntregaBairro { get; set; }
        public string? EntregaCidade { get; set; }
        public string? EntregaEstado { get; set; }
        public string? EntregaCep { get; set; }

        // Parada ativa na fila do motoboy: ordem real da rota e marcos da entrega.
        public int? RoutePosition { get; set; }
        public string? RouteStopStatus { get; set; }
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        public DateTimeOffset? ArrivedAtUtc { get; set; }

        // Ultima tentativa sem sucesso (nao entregue / recusado), para o atendente
        // saber por que o pedido voltou a ficar pendente.
        public string? LastFailureReason { get; set; }
        public string? LastFailureKind { get; set; }
        public DateTimeOffset? LastFailureAtUtc { get; set; }
        public int? LastFailureMotoboyId { get; set; }
        public string? LastFailureMotoboyNome { get; set; }
        /// <summary>Quantas vezes o pedido ja voltou a Pendente por nao entregue ou recusa.</summary>
        public int AttemptCount { get; set; }

        // Desfechos do dia: o mapa passou a devolver tambem os pedidos entregues e
        // cancelados hoje, para o painel mostrar "Entregue" em vez de sumir com eles.
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public int? CompletedByMotoboyId { get; set; }
        public string? CompletedByMotoboyNome { get; set; }
        public DateTimeOffset? CanceledAtUtc { get; set; }

        /// <summary>Ha transferencia aguardando aprovacao do estabelecimento para este pedido.</summary>
        public bool HasPendingTransfer { get; set; }
        public string? PendingTransferToNome { get; set; }
    }

    public class DeliveryDayMetricsDto
    {
        public int DeliveredToday { get; set; }
        public int FailedToday { get; set; }
        public double? AvgDeliveryMinutesToday { get; set; }
        public int PendingTransferApprovals { get; set; }
    }

    public class MotoboyDayStatsDto
    {
        public int MotoboyId { get; set; }
        public int DeliveredToday { get; set; }
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
