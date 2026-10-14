using System.Text.Json;
using System.Text.Json.Nodes;

namespace APIBack.Integrations.Ifood.Orders;

public sealed record IfoodPatchResult(IfoodImportedOrder Order, bool Complete);

public static class IfoodOrderPatch
{
    public static IfoodPatchResult Apply(string previous, JsonElement metadata, Guid merchant, Guid order)
    {
        try
        {
            var root = JsonNode.Parse(IfoodOrderNormalizer.Sanitize(previous, merchant, order))!.AsObject();
            var patch = JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? throw Invalid();
            if (patch["changes"] is not JsonArray changes || changes.Count == 0) throw Invalid();
            var items = root["items"]!.AsArray();
            foreach (var change in changes) Change(items, change as JsonObject ?? throw Invalid(), false);
            var total = root["total"]!.AsObject();
            if (patch["total"] is JsonObject totals)
                foreach (var pair in totals)
                {
                    var key = pair.Key == "subtotal" ? "subTotal" : pair.Key;
                    if (key is not ("subTotal" or "orderAmount" or "deliveryFee" or "additionalFees" or "benefits")) throw Invalid();
                    Set(total, key, pair.Value);
                }
            // Valores só são atuais quando o evento fornece total e saldos oficiais, sem cálculo local.
            var complete = patch["total"] is JsonObject amounts && amounts.ContainsKey("orderAmount") &&
                patch["payments"] is JsonObject payments && payments.ContainsKey("prepaid") && payments.ContainsKey("pending") && payments.ContainsKey("methods");
            if (patch["payments"] is JsonObject paymentPatch) root["payments"] = paymentPatch.DeepClone();
            else root["payments"] = new JsonObject();
            var normalized = IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, order);
            return new(normalized, complete && !normalized.RequiresReview);
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (FormatException) { throw Invalid(); }
    }
    private static void Change(JsonArray rows, JsonObject change, bool options)
    {
        var type = change["changeType"]?.ToString();
        var changes = change[options ? "options" : "items"] as JsonArray ?? throw Invalid();
        if (changes.Count == 0 || (options ? type is not ("ADD_OPTIONS" or "EDIT_OPTIONS" or "DELETE_OPTIONS") :
            type is not ("ADD_ITEMS" or "EDIT_ITEMS" or "DELETE_ITEMS"))) throw Invalid();
        foreach (var raw in changes)
        {
            var data = raw as JsonObject ?? throw Invalid();
            var unique = data["uniqueId"]?.ToString(); var id = data["id"]?.ToString();
            if (unique == null && id == null) throw Invalid();
            var matches = rows.OfType<JsonObject>().Where(r => unique != null ? r["uniqueId"]?.ToString() == unique : r["id"]?.ToString() == id).ToArray();
            if (type == (options ? "ADD_OPTIONS" : "ADD_ITEMS"))
            {
                if (matches.Length != 0 || data["name"] == null || data["quantity"] == null || data["unitPrice"] == null ||
                    data[options ? "price" : "totalPrice"] == null) throw Invalid();
                rows.Add(data.DeepClone());
            }
            else if (type == (options ? "DELETE_OPTIONS" : "DELETE_ITEMS"))
            {
                if (matches.Length != 1) throw Invalid();
                rows.Remove(matches[0]);
            }
            else if (type == (options ? "EDIT_OPTIONS" : "EDIT_ITEMS"))
            {
                if (matches.Length != 1 || data["changes"] is not JsonObject fields) throw Invalid();
                var target = matches[0];
                // Mudar quantidade/adicionais sem preço oficial deixa cobrança ambígua.
                if (!fields.ContainsKey(options ? "price" : "totalPrice")) throw Invalid();
                foreach (var field in fields)
                    if (field.Key == "optionsChanges")
                    {
                        if (options || field.Value is not JsonArray optionChanges || target["options"] is not JsonArray optionRows) throw Invalid();
                        foreach (var optionChange in optionChanges) Change(optionRows, optionChange as JsonObject ?? throw Invalid(), true);
                    }
                    else if (field.Key is "quantity" or "unitPrice" or "price" or "optionsPrice" or "totalPrice" or "observations" or "name" or "unit")
                        Set(target, field.Key, field.Value);
                    else throw Invalid();
            }
            else throw Invalid();
        }
    }
    private static void Set(JsonObject target, string key, JsonNode? value)
    {
        if (value is not JsonObject delta || !delta.ContainsKey("to") || !delta.ContainsKey("from") ||
            !JsonNode.DeepEquals(target[key], delta["from"])) throw Invalid();
        target[key] = delta["to"]?.DeepClone();
    }
    private static IfoodIntegrationException Invalid() => new("IFOOD_PATCH_REQUIRES_RECONCILIATION");
}
