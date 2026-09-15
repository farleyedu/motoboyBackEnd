using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    public sealed class AssignPedidoRequest
    {
        public int MotoboyId { get; set; }
    }

    public sealed class CancelPedidoRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class ReorderQueueRequest
    {
        public long ExpectedVersion { get; set; }
        public List<int> PedidoIdsOrdenados { get; set; } = new();
    }

    public sealed class RouteStopDto
    {
        public int PedidoId { get; set; }
        public int Position { get; set; }
        /// <summary>'assigned' (na fila) ou 'en_route' (entrega atual).</summary>
        public string Status { get; set; } = "assigned";
        public DateTimeOffset AssignedAtUtc { get; set; }
    }

    public sealed class MotoboyQueueDto
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public long Version { get; set; }
        public RouteStopDto? Current { get; set; }
        public List<RouteStopDto> Next { get; set; } = new();
    }
}
