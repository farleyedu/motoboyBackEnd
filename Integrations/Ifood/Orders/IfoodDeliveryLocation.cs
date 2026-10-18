using System.Globalization;
using System.Text.Json.Nodes;
using APIBack.Service.Interface;

namespace APIBack.Integrations.Ifood.Orders;

// Dados locais separados do endereço oficial. Nunca são aceitos da API iFood.
public static class IfoodDeliveryLocation
{
    public const string Key = "zippyDeliveryLocation";
    public static bool ValidPoint(double? latitude, double? longitude) =>
        latitude is { } lat && longitude is { } lon && double.IsFinite(lat) && double.IsFinite(lon)
        && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 && (Math.Abs(lat) > .0001 || Math.Abs(lon) > .0001);

    public static string Preserve(string incoming, string previous)
    {
        var root = JsonNode.Parse(incoming)!.AsObject();
        root.Remove(Key);
        var before = JsonNode.Parse(previous);
        if (before?[Key] is { } local && (local["source"]?.ToString() != "geocoded" ||
            JsonNode.DeepEquals(root["delivery"]?["deliveryAddress"], before["delivery"]?["deliveryAddress"]))) root[Key] = local.DeepClone();
        return root.ToJsonString();
    }

    public static string Set(string payload, JsonObject address, string source, int? actor, string note)
    {
        var root = JsonNode.Parse(payload)!.AsObject();
        root[Key] = new JsonObject { ["address"] = address.DeepClone(), ["source"] = source,
            ["actorUserId"] = actor, ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["note"] = note };
        return root.ToJsonString();
    }

    public static async Task<string> TryLocateAsync(string payload, Guid merchant, Guid order,
        IServiceScopeFactory scopes, CancellationToken ct)
    {
        var normalized = IfoodOrderNormalizer.Normalize(payload, merchant, order);
        if (normalized.OrderType != "DELIVERY" || normalized.DeliveredBy != "MERCHANT" ||
            !normalized.ReviewReasons.Contains("IFOOD_DELIVERY_LOCATION_REQUIRED") ||
            JsonNode.Parse(payload)?[Key]?["address"] != null) return payload;
        var address = JsonNode.Parse(normalized.Address.GetRawText()) as JsonObject;
        if (address == null || new[] { "streetName", "streetNumber", "city" }.Any(key => string.IsNullOrWhiteSpace(address[key]?.ToString()))) return payload;
        ct.ThrowIfCancellationRequested();
        using var scope = scopes.CreateScope();
        // Aproximações precisam de confirmação do atendente.
        var lookup = await scope.ServiceProvider.GetRequiredService<ILocalizacaoService>().ConsultarEnderecoAsync(
            string.Join(", ", new[] { "streetName", "streetNumber", "neighborhood", "city", "state", "postalCode" }.Select(key => address[key]?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s))),
            address["postalCode"]?.ToString());
        var found = lookup.Resultado;
        ct.ThrowIfCancellationRequested();
        if (found is not { Exata: true } ||
            !double.TryParse(found.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(found.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || !ValidPoint(lat, lon))
        {
            var root = JsonNode.Parse(payload)!.AsObject();
            root[Key] = new JsonObject { ["lookupStatus"] = lookup.Status, ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O") };
            return root.ToJsonString();
        }
        address["coordinates"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon };
        address["formattedAddress"] ??= string.Join(", ", new[] { "streetName", "streetNumber", "neighborhood", "city", "state" }.Select(key => address[key]?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
        return Set(payload, address, "geocoded", null, "Localização exata obtida automaticamente.");
    }
}
