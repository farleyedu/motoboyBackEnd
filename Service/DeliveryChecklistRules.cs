using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.DTOs.Delivery;

namespace APIBack.Service;

public static class DeliveryChecklistRules
{
    public const int MinManualReasonLength = 10;

    public static DeliveryChecklist BuildIfood(APIBack.Integrations.Ifood.Orders.IfoodImportedOrder order)
    {
        var items = new List<DeliveryChecklistItem>();
        for (var index = 0; index < order.Items.Count; index++)
        {
            var product = order.Items[index];
            var key = "ifood:" + index;
            // Foto e aviso vêm do vínculo ao cardápio (item sem vínculo sai sem aviso). O aviso entra na versão; a foto, não.
            items.Add(new() { Key = key, Name = product.Name, Quantity = product.Quantity, Unit = product.Unit, Note = product.Observations,
                ImageUrl = product.ImagemUrl, Extra = product.AtencaoMotoboy });
            if (product.Options.ValueKind != JsonValueKind.Array) continue;
            var optionIndex = 0;
            foreach (var option in product.Options.EnumerateArray())
            {
                var name = option.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var quantity = option.TryGetProperty("quantity", out value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : (decimal?)null;
                if (name == null || quantity == null || quantity <= 0)
                    throw new DeliveryDomainException(409, "IFOOD_DELIVERY_REVIEW_REQUIRED", "Confira os adicionais oficiais antes da coleta.");
                items.Add(new() { Key = key + ":option:" + optionIndex++, Name = name, ParentName = product.Name, Quantity = quantity.Value });
            }
        }
        // A versão é calculada sobre uma cópia sem foto: sem vínculo (foto e aviso vazios) o texto é idêntico ao de antes do vínculo, então
        // conferências já feitas continuam valendo; trocar o aviso de bag (Extra) muda a versão e pede nova conferência (CHECKLIST_CHANGED).
        var versioned = items.Select(i => new DeliveryChecklistItem { Key = i.Key, Name = i.Name, ParentName = i.ParentName,
            Quantity = i.Quantity, Unit = i.Unit, ImageUrl = null, Note = i.Note, Extra = i.Extra }).ToList();
        return new() { Items = items, Version = Digest(JsonSerializer.Serialize(versioned)), DetailsUnavailable = items.Count == 0 };
    }

    /// <summary>Pedido iFood cujo detalhe oficial não pôde ser lido: só a conferência manual justificada libera a coleta.</summary>
    public static DeliveryChecklist IfoodUnavailable() => new()
    {
        Items = new(), Version = Digest("ifood:details-unavailable"), DetailsUnavailable = true, RequiresManualReason = true
    };

    public static DeliveryChecklist Build(IReadOnlyList<PedidoItemDto> products)
    {
        var items = new List<DeliveryChecklistItem>();
        var occurrences = new Dictionary<string, int>();
        foreach (var product in products)
        {
            var identity = product.ItemId ?? Digest(JsonSerializer.Serialize(new { product.ProdutoId, product.Nome, product.Observacao }));
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            var prefix = identity + ":" + occurrence;
            items.Add(new() { Key = prefix, Name = product.Nome, Quantity = Math.Max(1, product.Quantidade), ImageUrl = product.ImagemUrl, Note = product.Observacao, Extra = product.AtencaoMotoboy });
            for (var index = 0; index < product.Adicionais.Count; index++)
            {
                var extra = product.Adicionais[index];
                items.Add(new() { Key = prefix + ":extra:" + index, Name = extra.Nome, ParentName = product.Nome, Quantity = checked(Math.Max(1, product.Quantidade) * Math.Max(1, extra.Quantidade)), Extra = extra.AtencaoMotoboy });
            }
        }
        var signature = JsonSerializer.Serialize(items.Select(i => new { i.Key, i.Name, i.ParentName, i.Quantity, i.Note, i.Extra }));
        return new() { Items = items, Version = Digest(signature), DetailsUnavailable = items.Count == 0 };
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static void Validate(int pedido, DeliveryChecklist expected, DeliveryChecklistConfirmation? confirmation)
    {
        // Contratos legados sem detalhamento permanecem operáveis. O app apresenta
        // conferência manual explícita; nunca descreve itens inexistentes.
        if (expected.RequiresManualReason &&
            (confirmation == null || (confirmation.ManualReason ?? string.Empty).Trim().Length < MinManualReasonLength))
            throw new DeliveryDomainException(422, "CHECKLIST_MANUAL_REASON_REQUIRED",
                "Os itens oficiais do iFood não estão disponíveis. Informe o motivo da conferência manual (mínimo " + MinManualReasonLength + " caracteres).");
        if (confirmation == null && expected.DetailsUnavailable) return;
        if (confirmation == null || confirmation.PedidoId != pedido || confirmation.Version != expected.Version)
            throw new DeliveryDomainException(409, "CHECKLIST_CHANGED", "Os itens do pedido mudaram. Confira novamente antes de continuar.");
        if (confirmation.ConfirmedKeys == null || confirmation.RecheckedExtraKeys == null ||
            confirmation.ConfirmedKeys.Count > 1000 || confirmation.RecheckedExtraKeys.Count > 1000 ||
            confirmation.ConfirmedKeys.Distinct().Count() != confirmation.ConfirmedKeys.Count ||
            confirmation.RecheckedExtraKeys.Distinct().Count() != confirmation.RecheckedExtraKeys.Count)
            throw new DeliveryDomainException(422, "CHECKLIST_INVALID", "Confira os itens e as quantidades do pedido.");
        var keys = expected.DetailsUnavailable ? new[] { "manual" } : expected.Items.Select(i => i.Key);
        var extras = expected.Items.Where(i => i.Extra).Select(i => i.Key);
        if (!keys.ToHashSet().SetEquals(confirmation.ConfirmedKeys) || !extras.ToHashSet().SetEquals(confirmation.RecheckedExtraKeys))
            throw new DeliveryDomainException(422, "CHECKLIST_INCOMPLETE", "Confira todos os itens e confirme novamente os extras.");
    }
}
