using System.Text.Json;
using APIBack.Integrations.Ifood.Events;

namespace APIBack.Integrations.Ifood.Orders;

public sealed record IfoodOrderProjectionEvent(string Code, DateTimeOffset At, [property:System.Text.Json.Serialization.JsonIgnore] JsonElement Metadata)
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["PLC"]="PLACED", ["CFM"]="CONFIRMED", ["PRS"]="PREPARATION_STARTED", ["SPS"]="SEPARATION_STARTED",
        ["RTP"]="READY_TO_PICKUP", ["SPE"]="SEPARATION_ENDED", ["DSP"]="DISPATCHED", ["CON"]="CONCLUDED",
        ["CAN"]="CANCELLED", ["CAR"]="CANCELLATION_REQUESTED", ["CARF"]="CANCELLATION_REQUEST_FAILED",
        ["OPA"]="ORDER_PATCHED", ["HSD"]="HANDSHAKE_DISPUTE", ["HSS"]="HANDSHAKE_SETTLEMENT",
        ["ADR"]="ASSIGN_DRIVER", ["GTO"]="GOING_TO_ORIGIN", ["AAO"]="ARRIVED_AT_ORIGIN", ["CLT"]="COLLECTED",
        ["AAD"]="ARRIVED_AT_DESTINATION", ["DRGO"]="DELIVERY_RETURNING_TO_ORIGIN", ["DRDO"]="DELIVERY_RETURNED_TO_ORIGIN",
        ["DRCR"]="DELIVERY_RETURN_CODE_REQUESTED", ["RPS"]="RECOMMENDED_PREPARATION_START", ["DGAC"]="DELIVERY_GROUP_ASSOCIATED",
        ["DAR"]="DELIVERY_ADDRESS_CHANGE_REQUESTED"
    };
    public static string Canonical(string code) => Codes.GetValueOrDefault(code) ?? code;
    public static IfoodOrderProjectionEvent Parse(IfoodInboxReservation item)
    {
        try
        {
            using var json = JsonDocument.Parse(item.PayloadJson);
            var raw = json.RootElement;
            var envelope = IfoodEventEnvelope.Parse(raw);
            if (envelope.EventId != item.EventId || envelope.MerchantId != item.MerchantId || envelope.OrderId != item.ExternalOrderId ||
                envelope.Code != item.EventCode || item.ExternalOrderId is null || envelope.RequiresReview)
                throw new IfoodIntegrationException("IFOOD_EVENT_INVALID_IDENTITY");
            var code = Canonical(item.EventCode);
            var full = IfoodOrderNormalizer.Text(raw, "fullCode");
            if (full != null && Canonical(full) != code) throw new IfoodIntegrationException("IFOOD_EVENT_CODE_CONFLICT");
            var date = IfoodOrderNormalizer.Text(raw, "createdAt");
            // Uma data sem timezone não serve como ordem oficial dos eventos.
            if (date == null || !System.Text.RegularExpressions.Regex.IsMatch(date, @"(Z|[+-]\d{2}:\d{2})$") || envelope.OccurredAtUtc == null)
                throw new IfoodIntegrationException("IFOOD_EVENT_MISSING_TIMESTAMP");
            return new(code, envelope.OccurredAtUtc.Value, IfoodOrderNormalizer.Field(raw, "metadata").ValueKind == JsonValueKind.Object
                ? raw.GetProperty("metadata").Clone() : JsonSerializer.SerializeToElement(new { }));
        }
        catch (JsonException) { throw new IfoodIntegrationException("IFOOD_EVENT_INVALID_PAYLOAD"); }
    }
    public override string ToString() => $"iFood {Code}; metadata omitido";
}

public static class IfoodProjectionOrdering
{
    // Ordenação por dimensão: avanço de preparo não prova despacho nem entrega física.
    public static string Decide(DateTimeOffset at, DateTimeOffset? previousAt, string? current, string next, Func<string, int>? rank = null)
    {
        if (previousAt.HasValue && at < previousAt) return "ignored";
        if (previousAt.HasValue && at == previousAt) return current == next ? "ignored" : "review";
        if (rank != null && current != null && rank(next) < rank(current)) return "review";
        return "applied";
    }
    public static int StatusRank(string value) => value switch { "PLACED"=>0,"CONFIRMED"=>1,"DISPATCHED"=>2,"CONCLUDED"=>3,"CANCELLED"=>4,_=>-1 };
    public static int PreparationRank(string value) => value switch { "awaiting_acceptance"=>0,"accepted"=>1,"preparing"=>2,"ready"=>3,_=>-1 };
}
