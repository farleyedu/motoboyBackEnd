using APIBack.Integrations.Ifood.Orders;

namespace APIBack.DTOs.Delivery;

public sealed class IfoodPedidoIdentityDto
{
    public string Provider { get; set; } = "ifood";
    public Guid MerchantId { get; set; }
    public Guid ExternalOrderId { get; set; }
    public string? DisplayNumber { get; set; }
    public string DisplayLabel => DisplayNumber == null ? "iFood · número indisponível" : $"iFood #{DisplayNumber}";
    public string OrderType { get; set; } = "";
    public string? DeliveredBy { get; set; }
    public decimal? OrderAmount { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? DeliveryFee { get; set; }
    public int ItemCount { get; set; }
    public decimal? PrepaidAmount { get; set; }
    public decimal? PendingAmount { get; set; }
    public long Revision { get; set; }
    public string? RemoteStatus { get; set; }
    public string? PreparationState { get; set; }
    public string? SynchronizationState { get; set; }
    public string? CancellationState { get; set; }
    public string? DeliveryState { get; set; }
    public bool DataIsCurrent { get; set; } = true;
    public IfoodCommandDto? ActiveCommand { get; set; }
}

public sealed class IfoodDisputeSummaryDto
{
    public Guid DisputeId { get; set; }
    public string State { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string TimeoutAction { get; set; } = "";
    public string? Action { get; set; }
    public string? HandshakeType { get; set; }
    public string? SettlementStatus { get; set; }
    public long Revision { get; set; }
    public Guid? ResponseOperationId { get; set; }
}
