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

    public sealed class LockPedidosRequest
    {
        public List<int> PedidoIds { get; set; } = new();
    }

    /// <summary>Filas afetadas (uma por motoboy) depois de travar/destravar.</summary>
    public sealed class LockPedidosResultDto
    {
        public List<MotoboyQueueDto> Filas { get; set; } = new();
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
        /// <summary>Motoboy confirmou a coleta no restaurante.</summary>
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        /// <summary>Motoboy informou que chegou no cliente.</summary>
        public DateTimeOffset? ArrivedAtUtc { get; set; }
        /// <summary>Pedido travado pelo estabelecimento: ancora na fila; so o estabelecimento destrava.</summary>
        public bool Locked { get; set; }
        /// <summary>Tudo que o app precisa para executar a entrega.</summary>
        public DeliveryStopOrderDto? Pedido { get; set; }
    }

    /// <summary>
    /// Dados do pedido que o motoboy precisa para entregar. Nunca inclui o codigo de
    /// entrega em si, apenas se ele e exigido.
    /// </summary>
    public sealed class DeliveryStopOrderDto
    {
        public int Id { get; set; }
        public string? NomeCliente { get; set; }
        public string? TelefoneCliente { get; set; }
        public string? EnderecoEntrega { get; set; }
        public string? Rua { get; set; }
        public string? Numero { get; set; }
        public string? Bairro { get; set; }
        public string? Cidade { get; set; }
        public string? Estado { get; set; }
        public string? Cep { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double[]? Coordinates => Longitude.HasValue && Latitude.HasValue
            ? new[] { Longitude.Value, Latitude.Value }
            : null;
        public string? Items { get; set; }
        public decimal? Value { get; set; }
        public string? TipoPagamento { get; set; }
        public string? StatusPagamento { get; set; }
        public decimal? Troco { get; set; }
        public string? Observacoes { get; set; }
        public DateTime? PrevisaoEntrega { get; set; }
        public bool RequerCodigoEntrega { get; set; }
    }

    public sealed class MotoboyQueueDto
    {
        public int MotoboyId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public long Version { get; set; }
        public RouteStopDto? Current { get; set; }
        public List<RouteStopDto> Next { get; set; } = new();
        /// <summary>'idle' ou 'returning' (voltando a loja depois da ultima entrega).</summary>
        public string RouteState { get; set; } = "idle";
        public DateTimeOffset? ReturningSinceUtc { get; set; }
        /// <summary>Regras do estabelecimento que o app precisa respeitar/exibir.</summary>
        public DeliveryPoliciesDto? Politicas { get; set; }
    }
}
