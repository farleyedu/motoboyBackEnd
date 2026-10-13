namespace APIBack.Integrations.Ifood.Contracts;

/// <summary>
/// Snapshot interno mínimo; não é o DTO HTTP do iFood nem calcula saldo após recebimentos locais.
/// Valores ausentes e modalidades desconhecidas permanecem explícitos para revisão.
/// </summary>
public sealed record IfoodOrderSnapshot
{
    public IfoodOrderIdentity Identity { get; }
    public long Revision { get; }
    public string OrderType { get; }
    public string? DeliveredBy { get; }
    public string? RemoteStatus { get; }
    public string? Currency { get; }
    public decimal? OrderAmount { get; }
    public decimal? PrepaidAmount { get; }
    public decimal? PendingAmount { get; }
    public DateTimeOffset? ScheduledStart { get; }
    public DateTimeOffset? ScheduledEnd { get; }

    public IfoodOrderSnapshot(IfoodOrderIdentity identity, long revision, string orderType,
        string? deliveredBy, string? remoteStatus, string? currency, decimal? orderAmount,
        decimal? prepaidAmount, decimal? pendingAmount, DateTimeOffset? scheduledStart = null,
        DateTimeOffset? scheduledEnd = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(orderType)) throw new ArgumentException("Modalidade obrigatória.", nameof(orderType));
        ValidateMoney(orderAmount, nameof(orderAmount));
        ValidateMoney(prepaidAmount, nameof(prepaidAmount));
        ValidateMoney(pendingAmount, nameof(pendingAmount));
        if (scheduledStart.HasValue != scheduledEnd.HasValue || scheduledEnd < scheduledStart)
            throw new ArgumentException("Janela agendada incompleta ou invertida.", nameof(scheduledEnd));
        Identity = identity;
        Revision = revision;
        OrderType = orderType.Trim();
        DeliveredBy = Clean(deliveredBy);
        RemoteStatus = Clean(remoteStatus);
        Currency = Clean(currency);
        OrderAmount = orderAmount;
        PrepaidAmount = prepaidAmount;
        PendingAmount = pendingAmount;
        ScheduledStart = scheduledStart;
        ScheduledEnd = scheduledEnd;
    }

    // Classificação, não autorização de atribuição: status, endereço e capacidades serão revalidados pelo caso de uso.
    public bool IsMerchantDelivery => string.Equals(OrderType, "DELIVERY", StringComparison.OrdinalIgnoreCase)
        && string.Equals(DeliveredBy, "MERCHANT", StringComparison.OrdinalIgnoreCase);
    public bool RequiresPaymentReview => PendingAmount is null || !string.Equals(Currency, "BRL", StringComparison.OrdinalIgnoreCase);
    public decimal? ExternalAmountToCollect => RequiresPaymentReview ? null : PendingAmount;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void ValidateMoney(decimal? value, string parameter)
    {
        if (value is { } amount && (amount < 0 || amount > 9_999_999_999_999_999.99m || decimal.Round(amount, 2) != amount))
            throw new ArgumentOutOfRangeException(parameter, "Valor deve ser não negativo, caber em NUMERIC(18,2) e não conter fração de centavo.");
    }
}
