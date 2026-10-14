namespace APIBack.DTOs.Delivery;

public sealed class IfoodCompletionOperationDto
{
    public Guid OperationId { get; set; }
    public int PedidoId { get; set; }
    public string State { get; set; } = "queued";
    public string Kind { get; set; } = "delivery";
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SendStartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public bool CodeChecked { get; set; }
    public DeliveryReceipt? Receipt { get; set; }
    public IfoodPickupReceiptDto? PickupReceipt { get; set; }
}

public sealed class IfoodPickupCompletionRequest
{
    public Guid OperationId { get; set; }
    public long ExpectedRevision { get; set; }
    public string Codigo { get; set; } = "";
    public List<DeliveryPaymentPart> Payments { get; set; } = new();
}
public sealed class IfoodPickupReceiptDto
{
    public Guid OperationId { get; set; }
    public int PedidoId { get; set; }
    public string? ExternalDisplayNumber { get; set; }
    public DateTimeOffset ConfirmedAtUtc { get; set; }
    public bool CodeChecked { get; set; }
    public string? OfficialConfirmationEvidence { get; set; }
    public decimal? OfficialOrderTotal { get; set; }
    public decimal AmountCollected { get; set; }
    public List<DeliveryPaymentPart> Payments { get; set; } = new();
}
public sealed class IfoodPickupCompletionContext
{
    public IfoodPedidoIdentityDto IdentidadeExterna { get; set; } = new();
    public bool RequiresOfficialConfirmation { get; set; } = true;
    public string? BlockedCode { get; set; }
    public IfoodCompletionOperationDto? Operation { get; set; }
}
