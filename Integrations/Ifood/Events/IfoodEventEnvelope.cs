using System.Text.Json;
using System.Text.Json.Serialization;

namespace APIBack.Integrations.Ifood.Events;

public sealed class IfoodEventEnvelope
{
    public Guid? EventId { get; }
    public Guid? MerchantId { get; }
    public Guid? OrderId { get; }
    public string Code { get; }
    public DateTimeOffset? OccurredAtUtc { get; }
    public bool RequiresReview { get; }
    [JsonIgnore] public string PayloadJson { get; }
    private IfoodEventEnvelope(Guid? id, Guid? merchant, Guid? order, string code, DateTimeOffset? at, bool review, string json)
        => (EventId, MerchantId, OrderId, Code, OccurredAtUtc, RequiresReview, PayloadJson) = (id, merchant, order, code, at, review, json);

    public static IfoodEventEnvelope Parse(JsonElement value)
    {
        static Guid? Id(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String &&
            Guid.TryParse(field.GetString(), out var id) && id != Guid.Empty ? id : null;
        var id = Id(value, "id"); var merchant = Id(value, "merchantId"); var order = Id(value, "orderId");
        var code = "__UNKNOWN__"; var review = false;
        DateTimeOffset? at = null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("code", out var field) && field.ValueKind == JsonValueKind.String &&
                field.GetString() is { Length: > 0 and <= 128 } raw && !raw.Any(char.IsControl) && !string.IsNullOrWhiteSpace(raw)) code = raw;
            else review = true;
            if (value.TryGetProperty("createdAt", out var time))
            {
                if (time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var date)) at = date.ToUniversalTime();
                else review = true;
            }
            if (value.TryGetProperty("orderId", out _) && order == null) review = true;
        }
        else review = true;
        return new(id, merchant, order, code, at, review, value.GetRawText());
    }
    public override string ToString() => "[evento iFood; payload omitido]";
}

public sealed class IfoodPollingMerchant
{
    public Guid EstabelecimentoId { get; set; }
    public Guid MerchantId { get; set; }
}

public sealed record IfoodEventReceipt(Guid MerchantId, Guid EstabelecimentoId, Guid EventId);
public sealed record IfoodPollingLease(string ApplicationKey, Guid LeaseId, DateTimeOffset NextPollAtUtc);
public sealed class IfoodInboxReservation
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid EstabelecimentoId { get; set; }
    public Guid EventId { get; set; }
    public Guid? ExternalOrderId { get; set; }
    public string EventCode { get; set; } = "";
    [JsonIgnore] public string PayloadJson { get; set; } = "{}";
    public int Attempts { get; set; }
    public Guid LeaseId { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public override string ToString() => "[reserva da inbox iFood; payload omitido]";
}
