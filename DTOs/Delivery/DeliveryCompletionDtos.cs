namespace APIBack.DTOs.Delivery;

public sealed class DeliveryPaymentPart
{
    public string Method { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? CashReceived { get; set; }
    public bool ReceivedConfirmed { get; set; }
}
public sealed class DeliveryCompletionRequest
{
    public Guid OperationId { get; set; }
    public int ExpectedPedidoId { get; set; }
    public long ExpectedVersion { get; set; }
    public string? Codigo { get; set; }
    public Guid? ProofId { get; set; }
    public List<DeliveryPaymentPart> Payments { get; set; } = new();
    public DeliveryChecklistConfirmation? Checklist { get; set; }
}
public sealed class DeliveryCodeRequest { public string Codigo { get; set; } = ""; }
public sealed class DeliveryProofRequest { public string Base64 { get; set; } = ""; }
public sealed class DeliveryCompletionContext
{
    public int PedidoId { get; set; }
    public long Version { get; set; }
    public string? NomeCliente { get; set; }
    public decimal? Total { get; set; }
    public bool RequiresCode { get; set; }
    public bool RequiresPayment { get; set; }
    public bool RequiresProof { get; set; }
    public DeliveryChecklist Checklist { get; set; } = new();
}
public sealed class DeliveryReceipt
{
    public Guid OperationId { get; set; }
    public int PedidoId { get; set; }
    public string? NomeCliente { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public decimal? Total { get; set; }
    public bool CodeChecked { get; set; }
    public bool PaidBeforeDelivery { get; set; }
    public List<DeliveryPaymentPart> Payments { get; set; } = new();
    public Guid? ProofId { get; set; }
    public DeliveryChecklistConfirmation? Checklist { get; set; }
}
public sealed class DeliveryCompletionResult
{
    public DeliveryReceipt Receipt { get; set; } = new();
    public MotoboyQueueDto Queue { get; set; } = new();
}
