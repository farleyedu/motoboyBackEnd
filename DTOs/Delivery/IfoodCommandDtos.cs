namespace APIBack.DTOs.Delivery;

public sealed class IfoodCommandRequest
{
    public Guid OperationId { get; set; }
    public long ExpectedRevision { get; set; }
    public string Command { get; set; } = "";
}
public sealed class IfoodCommandDto
{
    public Guid OperationId { get; set; }
    public int PedidoId { get; set; }
    public string Command { get; set; } = "";
    public string State { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NotBeforeUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public DateTimeOffset? SendStartedAtUtc { get; set; }
    public DateTimeOffset? HttpAcceptedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public Guid? ConfirmedEventId { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid? DisputeId { get; set; }
}
