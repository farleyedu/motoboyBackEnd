namespace APIBack.DTOs.Delivery;

public sealed record IfoodCancellationReasonDto(string Code,string Description);
public sealed class IfoodCancellationReasonsDto
{
    public Guid CatalogId { get; set; }
    public int PedidoId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public IReadOnlyList<IfoodCancellationReasonDto> Reasons { get; set; } = Array.Empty<IfoodCancellationReasonDto>();
}
public sealed class IfoodCancellationRequest
{
    public Guid OperationId { get; set; }
    public long ExpectedRevision { get; set; }
    public Guid CatalogId { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ParentOperationId { get; set; }
}
public sealed class IfoodDisputeResponseRequest
{
    public Guid OperationId { get; set; }
    public long ExpectedRevision { get; set; }
    public long ExpectedDisputeRevision { get; set; }
    public string Action { get; set; } = "";
    public string? Reason { get; set; }
    public Guid? AlternativeId { get; set; }
    public string? AmountMinor { get; set; }
    public string? Currency { get; set; }
    public int? AdditionalTimeInMinutes { get; set; }
    public Guid? ParentOperationId { get; set; }
}
public sealed class IfoodDisputeOptionsDto
{
    public Guid DisputeId { get; set; }
    public long Revision { get; set; }
    public long OrderRevision { get; set; }
    public string State { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string TimeoutAction { get; set; } = "";
    public bool CanRespond { get; set; }
    public Guid? ResponseOperationId { get; set; }
    public IReadOnlyList<string> Actions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> AcceptReasons { get; set; } = Array.Empty<string>();
    public IReadOnlyList<IfoodDisputeAlternativeDto> Alternatives { get; set; } = Array.Empty<IfoodDisputeAlternativeDto>();
}
public sealed record IfoodDisputeAlternativeDto(Guid Id,string Type,string? MaxAmountMinor,string? Currency,
    IReadOnlyList<int> AllowedMinutes,IReadOnlyList<string> AllowedReasons);
