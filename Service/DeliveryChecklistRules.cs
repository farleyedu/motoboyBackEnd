using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIBack.DTOs.Delivery;

namespace APIBack.Service;

public static class DeliveryChecklistRules
{
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
