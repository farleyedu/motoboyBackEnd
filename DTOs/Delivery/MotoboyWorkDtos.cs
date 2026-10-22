namespace APIBack.DTOs.Delivery;

public sealed record RiderPayPlan(Guid Id, string Mode, decimal Rate, DateTimeOffset CreatedAtUtc);
public sealed class RiderPayPlanRequest
{
    public Guid OperationId { get; set; }
    public Guid? ExpectedPlanId { get; set; }
    public string Mode { get; set; } = "delivery";
    public decimal Rate { get; set; }
}
/// <summary>Mesma regra para vários motoboys: cada item vira um plano próprio, com o id derivado de OperationId + motoboyId (reenvio idempotente).</summary>
public sealed class RiderPayPlanBatchRequest
{
    public Guid OperationId { get; set; }
    public string Mode { get; set; } = "delivery";
    public decimal Rate { get; set; }
    public List<RiderPayPlanBatchItem> Items { get; set; } = new();
}
public sealed class RiderPayPlanBatchItem
{
    public int MotoboyId { get; set; }
    public Guid? ExpectedPlanId { get; set; }
}
public sealed record RiderPayPlanBatchResult(int MotoboyId, bool Ok, RiderPayPlan? Plan, string? Code, string? Message);
public sealed record RiderSummaryBalance(decimal Outstanding, decimal CashToReturn);
/// <summary>
/// Uma linha por motoboy vinculado: totais do período (entregas, km, minutos, valor) e saldo/recebimentos de todo o histórico.
/// <c>Plan</c> é só a exceção individual ativa; <c>EffectivePlan</c> é a regra que vale de fato e <c>PlanSource</c> diz de onde ela vem:
/// "rider" (exceção do motoboy), "default" (padrão da loja) ou "none" (nenhuma regra: configure o padrão ou a do motoboy).
/// </summary>
public sealed record RiderSummaryRow(int MotoboyId, string Nome, RiderPayPlan? Plan, RiderSummaryBalance Balance, int UnconfirmedReceipts,
    int Entregas, decimal Km, int MinutosTrabalhados, decimal ValorGanho, string PlanSource = "none", RiderPayPlan? EffectivePlan = null);
public sealed record RiderWorkSummary(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<RiderSummaryRow> Riders, RiderPayPlan? DefaultPlan = null);
/// <summary>Padrão vigente da loja, seu histórico (mais recente primeiro) e quantos motoboys o herdam ou têm exceção.</summary>
public sealed record RiderDefaultPlanView(RiderPayPlan? Plan, IReadOnlyList<RiderPayPlan> History, int RidersUsingDefault, int RidersWithOverride);
/// <summary>Resultado de "voltar ao padrão": o motoboy passa a seguir a regra efetiva abaixo (nulo = a loja ainda não tem padrão).</summary>
public sealed record RiderOverrideResult(int MotoboyId, string PlanSource, RiderPayPlan? EffectivePlan, DateTimeOffset ResetAtUtc);
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
