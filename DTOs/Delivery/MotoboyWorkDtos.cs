namespace APIBack.DTOs.Delivery;

public sealed record RiderPayPlan(Guid Id, string Mode, decimal Rate, DateTimeOffset CreatedAtUtc);
public sealed class RiderPayPlanRequest
{
    public Guid OperationId { get; set; }
    public Guid? ExpectedPlanId { get; set; }
    public string Mode { get; set; } = "delivery";
    public decimal Rate { get; set; }
}
public sealed record RiderPayQuote(Guid PlanId, string Mode, decimal Rate, decimal? DistanceKm, decimal? Amount);
public sealed record RiderPeriodQuote(decimal Amount, long? TotalSeconds, long? WorkedSeconds);
public sealed class RiderPeriodRequest
{
    public Guid OperationId { get; set; }
    public Guid PlanId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int? WorkedMinutes { get; set; }
}
public sealed class RiderSettlementRequest { public Guid OperationId { get; set; } }
public sealed class RiderSupportRequest
{
    public Guid OperationId { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class RiderSettlementAction
{
    public string Action { get; set; } = "";
    public string? Reason { get; set; }
    public string? Method { get; set; }
    public string? Reference { get; set; }
}
public sealed class RiderWorkEntry
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string? Mode { get; set; }
    public decimal? Amount { get; set; }
    public decimal StoreCash { get; set; }
    public decimal? DistanceKm { get; set; }
    public int? PedidoId { get; set; }
    public long? StopId { get; set; }
    public Guid? SettlementId { get; set; }
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public int? WorkedMinutes { get; set; }
    public long? PeriodTotalSeconds { get; set; }
    public long? PeriodWorkedSeconds { get; set; }
    public bool Backfilled { get; set; }
}
public sealed class RiderSettlement
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "draft";
    public decimal Earnings { get; set; }
    public decimal StoreCash { get; set; }
    public bool CashReturned { get; set; }
    public string? Reason { get; set; }
    public string? Method { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
