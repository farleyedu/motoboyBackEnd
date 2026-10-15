using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace APIBack.Integrations.Ifood.Orders;

public sealed class IfoodImportedOrder
{
    public Guid ExternalOrderId { get; init; }
    public Guid MerchantId { get; init; }
    public string? DisplayNumber { get; init; }
    public string OrderType { get; init; } = "";
    public string? DeliveredBy { get; init; }
    public string? OrderTiming { get; init; }
    public string? Category { get; init; }
    public bool? IsTest { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? PreparationStartUtc { get; init; }
    public DateTimeOffset? ScheduledStartUtc { get; init; }
    public DateTimeOffset? ScheduledEndUtc { get; init; }
    public string? CustomerName { get; init; }
    public string? ContactNumber { get; init; }
    public string? Localizer { get; init; }
    public DateTimeOffset? LocalizerExpirationUtc { get; init; }
    public decimal OrderAmount { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? DeliveryFee { get; init; }
    public decimal? AdditionalFees { get; init; }
    public decimal? BenefitsAmount { get; init; }
    public decimal? PrepaidAmount { get; init; }
    public decimal? PendingAmount { get; init; }
    public string? Currency { get; init; }
    public string PaymentState { get; init; } = "requer_revisao";
    public bool RequiresReview { get; init; }
    public bool ImportedByRecovery { get; init; }
    public IReadOnlyList<string> ReviewReasons { get; init; } = Array.Empty<string>();
    [JsonIgnore] public bool CollectionAllowed { get; init; } = true;
    public decimal? AmountToCollect => !CollectionAllowed || PaymentState == "requer_revisao" ? null : PendingAmount;
    public string? Observations { get; init; }
    public JsonElement Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public JsonElement Benefits { get; init; }
    public IReadOnlyList<IfoodImportedItem> Items { get; init; } = Array.Empty<IfoodImportedItem>();
    public IReadOnlyList<IfoodImportedPayment> Payments { get; init; } = Array.Empty<IfoodImportedPayment>();
    [JsonIgnore] public string PayloadJson { get; init; } = "{}";
    public override string ToString() => "[pedido iFood; dados operacionais omitidos]";
}

public sealed record IfoodImportedItem(string? ExternalItemId, string Name, decimal Quantity, string? Unit,
    decimal? UnitPrice, decimal? Price, decimal? OptionsPrice, decimal? TotalPrice, string? Observations, JsonElement Options);
public sealed record IfoodImportedPayment(decimal? Value, string? Currency, string? Type, string? Method,
    string? CardBrand, string? Wallet, decimal? ChangeFor);

public static class IfoodOrderNormalizer
{
    // Não precisamos de documentos fiscais, segmentação nem códigos esperados para importar a operação.
    private static readonly HashSet<string> PrivateFields = new(StringComparer.OrdinalIgnoreCase)
    { "documentNumber", "documentType", "cpf", "segmentation", "ordersCountOnMerchant", "billingAddress",
      "pickupCode", "deliveryCode", "verificationCode", "verificationCodes", "cvv", "securityCode", "cardNumber", "transaction" };

    public static string Sanitize(string json, Guid expectedMerchant, Guid expectedOrder)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw Invalid("IFOOD_ORDER_INVALID_RESPONSE"); }
        catch (JsonException) { throw Invalid("IFOOD_ORDER_INVALID_RESPONSE"); }
        if (!Guid.TryParse(root["id"]?.ToString(), out var order) || order != expectedOrder || order == Guid.Empty ||
            !Guid.TryParse((root["merchant"] as JsonObject)?["id"]?.ToString(), out var merchant) || merchant != expectedMerchant || merchant == Guid.Empty)
            throw Invalid("IFOOD_ORDER_IDENTITY_MISMATCH");
        void RemovePrivate(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                    if (PrivateFields.Contains(pair.Key)) obj.Remove(pair.Key); else RemovePrivate(pair.Value);
            else if (node is JsonArray array) foreach (var child in array) RemovePrivate(child);
        }
        RemovePrivate(root);
        return root.ToJsonString();
    }

    public static IfoodImportedOrder Normalize(string json, Guid expectedMerchant, Guid expectedOrder, bool collectionAllowed = true)
    {
        var sanitized = Sanitize(json, expectedMerchant, expectedOrder);
        using var document = JsonDocument.Parse(sanitized);
        var root = document.RootElement;
        var total = Field(root, "total"); var payments = Field(root, "payments"); var customer = Field(root, "customer");
        var delivery = Field(root, "delivery"); var phone = Field(customer, "phone"); var schedule = Field(root, "schedule");
        var alternateSchedule = Field(root, "scheduled");
        if (schedule.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) schedule = alternateSchedule;
        var type = Text(root, "orderType") ?? "__UNKNOWN__";
        var timing = Text(root, "orderTiming"); var carrier = Text(delivery, "deliveredBy"); var category = Text(root, "category");
        var start = Date(schedule, "deliveryDateTimeStart"); var end = Date(schedule, "deliveryDateTimeEnd");
        var preparation = Date(root, "preparationStartDateTime") ?? Date(schedule, "preparationStartDateTime");
        var modalityReview = type is not ("DELIVERY" or "TAKEOUT" or "DINE_IN") || category != "FOOD" ||
            timing is not ("IMMEDIATE" or "SCHEDULED") || (type == "DELIVERY" && carrier is not ("MERCHANT" or "IFOOD"));
        var scheduleReview = (timing == "SCHEDULED" && (start == null || end == null || end < start)) ||
            (alternateSchedule.ValueKind == JsonValueKind.Object && Field(root,"schedule").ValueKind == JsonValueKind.Object &&
                (Date(alternateSchedule,"deliveryDateTimeStart") != start || Date(alternateSchedule,"deliveryDateTimeEnd") != end)) ||
            (Date(alternateSchedule,"preparationStartDateTime") is {} alternatePreparation && alternatePreparation != preparation) ||
            (Date(root,"preparationStartDateTime") is {} primaryPreparation && Date(schedule,"preparationStartDateTime") is {} schedulePreparation && primaryPreparation != schedulePreparation);
        var review = modalityReview || scheduleReview;
        var itemReview = false;
        var items = new List<IfoodImportedItem>();
        var itemArray = Field(root, "items");
        if (itemArray.ValueKind != JsonValueKind.Array || itemArray.GetArrayLength() == 0) throw Invalid("IFOOD_ORDER_INVALID_ITEMS");
        foreach (var item in itemArray.EnumerateArray())
        {
            var name = Text(item, "name") ?? throw Invalid("IFOOD_ORDER_INVALID_ITEMS");
            var quantity = Number(item, "quantity") ?? throw Invalid("IFOOD_ORDER_INVALID_ITEMS");
            if (quantity <= 0) throw Invalid("IFOOD_ORDER_INVALID_ITEMS");
            var options = Field(item, "options");
            if (options.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null)) itemReview = true;
            if (options.ValueKind == JsonValueKind.Array)
                foreach (var option in options.EnumerateArray())
                {
                    if (Text(option, "name") == null || Number(option, "quantity") is not > 0) itemReview = true;
                    _ = Money(option, "price"); _ = Number(option, "unitPrice");
                }
            var itemTotal = Money(item, "totalPrice"); var unitPrice = Number(item, "unitPrice");
            if (itemTotal == null || unitPrice == null) itemReview = true;
            items.Add(new(Text(item, "id"), name, quantity, Text(item, "unit"), unitPrice, Money(item, "price"),
                Money(item, "optionsPrice"), itemTotal, Text(item, "observations"), CloneOr(options, "[]")));
        }
        var methods = new List<IfoodImportedPayment>(); var methodArray = Field(payments, "methods");
        if (methodArray.ValueKind == JsonValueKind.Array)
            foreach (var method in methodArray.EnumerateArray())
                methods.Add(new(Money(method, "value"), Text(method, "currency"), Text(method, "type"), Text(method, "method"),
                    Text(Field(method, "card"), "brand"), Text(Field(method, "wallet"), "name"), Money(Field(method, "cash"), "changeFor")));
        else review = true;
        var prepaid = Money(payments, "prepaid"); var pending = Money(payments, "pending");
        var currencies = methods.Select(m => m.Currency).Distinct().ToArray();
        var currency = Text(payments, "currency") ?? (currencies.Length == 1 ? currencies[0] : null);
        var paymentReview = pending == null || prepaid == null || currency != "BRL" ||
            methods.Any(m => m.Value == null || m.Currency != "BRL" || m.Type is not ("ONLINE" or "OFFLINE") ||
                m.Method is not ("CASH" or "CREDIT" or "DEBIT" or "MEAL_VOUCHER" or "FOOD_VOUCHER" or "GIFT_CARD" or "DIGITAL_WALLET" or "PIX" or "OTHER"));
        if (!paymentReview)
        {
            paymentReview = methods.Where(m => m.Type == "ONLINE").Sum(m => m.Value ?? 0) != prepaid ||
                methods.Where(m => m.Type == "OFFLINE").Sum(m => m.Value ?? 0) != pending;
        }
        var address = Field(delivery, "deliveryAddress");
        var coordinates = Field(address, "coordinates");
        static double? Coordinate(JsonElement value, string name, double limit)
        {
            var field = Field(value, name);
            return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) &&
                double.IsFinite(number) && Math.Abs(number) <= limit ? number : null;
        }
        var latitude = Coordinate(coordinates, "latitude", 90);
        var longitude = Coordinate(coordinates, "longitude", 180);
        var reasons = new List<string>();
        if (modalityReview) reasons.Add("IFOOD_ORDER_MODALITY_REVIEW");
        if (scheduleReview) reasons.Add("IFOOD_ORDER_SCHEDULE_REVIEW");
        if (itemReview) { review = true; reasons.Add("IFOOD_ORDER_ITEMS_REVIEW"); }
        if (methodArray.ValueKind != JsonValueKind.Array && !paymentReview) reasons.Add("IFOOD_PAYMENT_DATA_REVIEW");
        if (paymentReview) reasons.Add("IFOOD_PAYMENT_DATA_REVIEW");
        if (type == "DELIVERY" && carrier == "MERCHANT" &&
            (Text(address, "streetName") == null || Text(address, "streetNumber") == null || Text(address, "city") == null || latitude == null || longitude == null))
        { review = true; reasons.Add("IFOOD_DELIVERY_LOCATION_REQUIRED"); }
        return new IfoodImportedOrder
        {
            ExternalOrderId = expectedOrder, MerchantId = expectedMerchant, DisplayNumber = Text(root, "displayId"), CollectionAllowed = collectionAllowed,
            OrderType = type, DeliveredBy = carrier, OrderTiming = timing, Category = category,
            IsTest = Field(root, "isTest").ValueKind is JsonValueKind.True or JsonValueKind.False ? Field(root, "isTest").GetBoolean() : null,
            ImportedByRecovery = Field(root, "zippyRecovery").ValueKind == JsonValueKind.True,
            ReviewReasons = reasons,
            CreatedAtUtc = Date(root, "createdAt") ?? throw Invalid("IFOOD_ORDER_INVALID_DATE"),
            PreparationStartUtc = preparation, ScheduledStartUtc = start, ScheduledEndUtc = end,
            CustomerName = Text(customer, "name"), ContactNumber = Text(phone, "number"), Localizer = Text(phone, "localizer"),
            LocalizerExpirationUtc = Date(phone, "localizerExpiration"),
            OrderAmount = Money(total, "orderAmount") ?? throw Invalid("IFOOD_ORDER_INVALID_TOTAL"), Subtotal = Money(total, "subTotal"),
            DeliveryFee = Money(total, "deliveryFee"), AdditionalFees = Money(total, "additionalFees"), BenefitsAmount = Money(total, "benefits"),
            PrepaidAmount = prepaid, PendingAmount = pending, Currency = currency,
            PaymentState = paymentReview ? "requer_revisao" : pending == 0 ? "pago" : prepaid > 0 ? "parcial" : "pendente",
            RequiresReview = review || paymentReview, Address = CloneOr(address, "null"), Latitude = latitude, Longitude = longitude, Benefits = CloneOr(Field(root, "benefits"), "[]"),
            Observations = string.Join("\n", new[] { Text(root, "extraInfo"), Text(delivery, "observations"), Text(Field(root, "takeout"), "observations") }.Where(v => v != null)),
            Items = items, Payments = methods, PayloadJson = sanitized
        };
    }

    internal static JsonElement Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    internal static string? Text(JsonElement value, string key)
    {
        var field = Field(value, key);
        return field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString()!.Trim() : null;
    }
    internal static decimal? Number(JsonElement value, string key)
    {
        var field = Field(value, key);
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDecimal(out var result) || result < 0)
            throw Invalid("IFOOD_ORDER_INVALID_NUMBER");
        return result;
    }
    private static decimal? Money(JsonElement value, string key)
    {
        var result = Number(value, key);
        if (result > 9_999_999_999_999_999.99m || (result.HasValue && decimal.Round(result.Value, 2) != result))
            throw Invalid("IFOOD_ORDER_INVALID_MONEY");
        return result;
    }
    private static DateTimeOffset? Date(JsonElement value, string key)
    {
        var field = Field(value, key);
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || !field.TryGetDateTimeOffset(out var result)) throw Invalid("IFOOD_ORDER_INVALID_DATE");
        return result.ToUniversalTime();
    }
    private static JsonElement CloneOr(JsonElement value, string fallback)
    {
        if (value.ValueKind != JsonValueKind.Undefined) return value.Clone();
        using var document = JsonDocument.Parse(fallback); return document.RootElement.Clone();
    }
    private static IfoodIntegrationException Invalid(string code) => new(code);
}
