using System;
using System.Collections.Generic;

namespace APIBack.DTOs.Delivery
{
    /// <summary>Leitura do pedido para seu entregador. Não expõe o código secreto nem permissões administrativas.</summary>
    public sealed class MotoboyPedidoDetalheDto
    {
        public DeliveryChecklist Checklist { get; set; } = new();
        public int Id { get; set; }
        public long QueueVersion { get; set; }
        public int Position { get; set; }
        public string StopStatus { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
        public bool IsOffer { get; set; }
        public bool Locked { get; set; }
        public DateTimeOffset AssignedAtUtc { get; set; }
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        public DateTimeOffset? ArrivedAtUtc { get; set; }
        public string Origem { get; set; } = string.Empty;
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
        public decimal? Total { get; set; }
        public decimal? Subtotal { get; set; }
        public decimal? TaxaEntrega { get; set; }
        public string? FormaPagamento { get; set; }
        public string? StatusPagamento { get; set; }
        public decimal? Troco { get; set; }
        public string? Observacoes { get; set; }
        public DateTime? PrevisaoEntrega { get; set; }
        public bool RequerCodigoEntrega { get; set; }
        public string? CapaImagemUrl { get; set; }
        public List<PedidoItemDto> Itens { get; set; } = new();
    }
}
