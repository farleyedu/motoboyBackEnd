using System;

namespace APIBack.Model.Delivery
{
    public sealed class RouteStopRecord
    {
        public long Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public int MotoboyId { get; set; }
        public int PedidoId { get; set; }
        public int Position { get; set; }
        public string StopStatus { get; set; } = "assigned";
        public DateTimeOffset AssignedAtUtc { get; set; }
    }

    public sealed class MotoboyRouteHeader
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public long Version { get; set; }
    }
}
