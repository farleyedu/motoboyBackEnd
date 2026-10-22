using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodItemLinkTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Order = Guid.NewGuid();

    private static string PayloadWithItem(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(IfoodOrderImportTests.Payload(Merchant, Order))!.AsObject();
        change(root["items"]!.AsArray()[0]!.AsObject());
        return root.ToJsonString();
    }

    private static IfoodImportedOrder Normalize(string payload) => IfoodOrderNormalizer.Normalize(payload, Merchant, Order);

    [Theory]
    [InlineData("X-Burger", "x-burger")]
    [InlineData("  X-BURGER  ", "x-burger")]
    [InlineData("X-Burger   Duplo\tBacon", "x-burger duplo bacon")]
    [InlineData("Coca\n Cola", "coca cola")]
    public void NameNormalizationIgnoresCaseAndSpaces(string input, string expected) =>
        Assert.Equal(expected, IfoodItemLinkRules.NormalizeName(input));

    [Fact]
    public void AccentStillDistinguishesNames() =>
        Assert.NotEqual(IfoodItemLinkRules.NormalizeName("Açaí"), IfoodItemLinkRules.NormalizeName("Acai"));

    [Fact]
    public void SqlExpressionMatchesTheAgreedRule() =>
        Assert.Equal(@"lower(btrim(regexp_replace(nome, '\s+', ' ', 'g')))", IfoodItemLinkRules.NameSql("nome"));

    [Fact]
    public void ConfirmedLinkByItemIdWinsOverNameEvenWhenNameMatchesAnotherProduct()
    {
        var linked = Guid.NewGuid(); var sameName = Guid.NewGuid();
        var decision = IfoodItemLinkRules.Decide("confirmado", linked, "Pizza", new[] { (sameName, "pizza") });
        Assert.Equal(IfoodLinkAction.UseExisting, decision.Action);
        Assert.Equal(linked, decision.ProdutoId);
    }

    [Fact]
    public void ExactlyOneProductWithTheSameNameConfirmsByName()
    {
        var product = Guid.NewGuid();
        var decision = IfoodItemLinkRules.Decide("pendente", null, "  PIZZA  calabresa", new[] { (product, "Pizza Calabresa"), (Guid.NewGuid(), "Pizza Mussarela") });
        Assert.Equal(IfoodLinkAction.AutoConfirm, decision.Action);
        Assert.Equal(product, decision.ProdutoId);
    }

    [Fact]
    public void NewKeyWithoutExistingRowAlsoMatchesByName()
    {
        var product = Guid.NewGuid();
        Assert.Equal(IfoodLinkAction.AutoConfirm, IfoodItemLinkRules.Decide(null, null, "Suco", new[] { (product, "suco") }).Action);
    }

    [Fact]
    public void DuplicateNamesStayPendingInsteadOfGuessing() =>
        Assert.Equal(IfoodLinkAction.Pending, IfoodItemLinkRules.Decide(null, null, "Suco", new[] { (Guid.NewGuid(), "Suco"), (Guid.NewGuid(), "SUCO") }).Action);

    [Fact]
    public void NoProductWithTheNameStaysPending() =>
        Assert.Equal(IfoodLinkAction.Pending, IfoodItemLinkRules.Decide("pendente", null, "Suco", new[] { (Guid.NewGuid(), "Suco de laranja") }).Action);

    [Fact]
    public void AccentedNameDoesNotMatchTheUnaccentedProduct() =>
        Assert.Equal(IfoodLinkAction.Pending, IfoodItemLinkRules.Decide(null, null, "Açaí", new[] { (Guid.NewGuid(), "Acai") }).Action);

    [Fact]
    public void UndoneByAPersonIsNeverRelinkedAutomatically() =>
        Assert.Equal(IfoodLinkAction.Untouched, IfoodItemLinkRules.Decide("desfeito", null, "Suco", new[] { (Guid.NewGuid(), "Suco") }).Action);

    [Fact]
    public void NormalizerReadsOptionalFieldsAndToleratesTheirAbsence()
    {
        var withFields = Normalize(PayloadWithItem(i =>
        {
            i["externalCode"] = " COD-9 "; i["imageUrl"] = "https://static.ifood-static.com.br/a.png"; i["uniqueId"] = "u-1"; i["ean"] = "789";
        })).Items[0];
        Assert.Equal("COD-9", withFields.ExternalCode);
        Assert.Equal("https://static.ifood-static.com.br/a.png", withFields.ImagemUrl);
        Assert.Equal("u-1", withFields.UniqueId); Assert.Equal("789", withFields.Ean);
        Assert.False(withFields.Vinculado); Assert.Null(withFields.ProdutoId);

        var without = Normalize(IfoodOrderImportTests.Payload(Merchant, Order)).Items[0];
        Assert.Null(without.ExternalCode); Assert.Null(without.ImagemUrl); Assert.Null(without.UniqueId); Assert.Null(without.Ean);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("a.png")]
    [InlineData("")]
    public void ImageThatIsNotAnAbsoluteWebAddressBecomesNoImageWithoutFailingTheImport(string value) =>
        Assert.Null(Normalize(PayloadWithItem(i => i["imageUrl"] = value)).Items[0].ImagemUrl);

    [Fact]
    public void NonTextOptionalFieldsAreIgnoredNotThrown()
    {
        var item = Normalize(PayloadWithItem(i => { i["externalCode"] = 12; i["imageUrl"] = new JsonObject(); i["ean"] = true; })).Items[0];
        Assert.Null(item.ExternalCode); Assert.Null(item.ImagemUrl); Assert.Null(item.Ean);
    }

    [Fact]
    public void ItemContractOnlyGainsOptionalFieldsAndKeepsInternalMetadataOut()
    {
        var json = JsonSerializer.Serialize(Normalize(IfoodOrderImportTests.Payload(Merchant, Order)).Items[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        foreach (var existing in new[] { "externalItemId", "name", "quantity", "unit", "unitPrice", "price", "optionsPrice", "totalPrice", "observations", "options" })
            Assert.True(item.TryGetProperty(existing, out _), existing);
        Assert.False(item.GetProperty("vinculado").GetBoolean());
        foreach (var optional in new[] { "produtoId", "imagemUrl", "categoriaNome", "externalCode" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(optional).ValueKind);
        Assert.False(item.GetProperty("atencaoMotoboy").GetBoolean());
        Assert.False(item.TryGetProperty("uniqueId", out _)); Assert.False(item.TryGetProperty("ean", out _));
    }

    [Fact]
    public void ApplyLinksOnlyAddsDisplayFieldsAndNeverTouchesMoneyOrQuantity()
    {
        var order = Normalize(IfoodOrderImportTests.Payload(Merchant, Order));
        var product = Guid.NewGuid();
        var links = JsonSerializer.Serialize(new Dictionary<string, object> { ["item-external"] = new { produtoId = product, imagemUrl = "https://loja/p.png", categoria = "Lanches", atencao = true } });
        var enriched = IfoodOrderReadModel.ApplyLinks(order, links);
        var before = order.Items[0]; var after = enriched.Items[0];
        Assert.True(after.Vinculado); Assert.Equal(product, after.ProdutoId); Assert.Equal("Lanches", after.CategoriaNome); Assert.True(after.AtencaoMotoboy);
        Assert.Equal("https://loja/p.png", after.ImagemUrl);
        Assert.Equal((before.Name, before.Quantity, before.Unit, before.UnitPrice, before.Price, before.OptionsPrice, before.TotalPrice, before.Observations),
            (after.Name, after.Quantity, after.Unit, after.UnitPrice, after.Price, after.OptionsPrice, after.TotalPrice, after.Observations));
        Assert.Equal((order.OrderAmount, order.PendingAmount, order.PrepaidAmount, order.Subtotal), (enriched.OrderAmount, enriched.PendingAmount, enriched.PrepaidAmount, enriched.Subtotal));
    }

    [Fact]
    public void IfoodImageTakesPriorityOverTheProductImage()
    {
        var order = Normalize(PayloadWithItem(i => i["imageUrl"] = "https://ifood/a.png"));
        var links = JsonSerializer.Serialize(new Dictionary<string, object> { ["item-external"] = new { produtoId = Guid.NewGuid(), imagemUrl = "https://loja/p.png" } });
        Assert.Equal("https://ifood/a.png", IfoodOrderReadModel.ApplyLinks(order, links).Items[0].ImagemUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("[1,2]")]
    [InlineData("{\"item-external\":{\"produtoId\":\"não-é-guid\"}}")]
    [InlineData("{\"outro-id\":{\"produtoId\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"}}")]
    public void UnreadableOrIrrelevantLinksLeaveTheItemsUntouched(string? links)
    {
        var order = Normalize(IfoodOrderImportTests.Payload(Merchant, Order));
        var result = IfoodOrderReadModel.ApplyLinks(order, links);
        Assert.False(result.Items[0].Vinculado);
        Assert.Equal(order.Items[0], result.Items[0]);
    }

    // Hash do checklist antes do vínculo existir: serialização dos itens (sem foto, sem aviso).
    private static string LegacyVersion(IfoodImportedOrder order)
    {
        var items = new List<DeliveryChecklistItem>();
        for (var index = 0; index < order.Items.Count; index++)
        {
            var product = order.Items[index]; var key = "ifood:" + index;
            items.Add(new() { Key = key, Name = product.Name, Quantity = product.Quantity, Unit = product.Unit, Note = product.Observations });
            var optionIndex = 0;
            foreach (var option in product.Options.EnumerateArray())
                items.Add(new() { Key = key + ":option:" + optionIndex++, Name = option.GetProperty("name").GetString()!, ParentName = product.Name, Quantity = option.GetProperty("quantity").GetDecimal() });
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items)))).ToLowerInvariant();
    }

    [Fact]
    public void ChecklistVersionForUnlinkedItemsIsIdenticalToTheOneBeforeLinks()
    {
        var order = Normalize(IfoodOrderImportTests.Payload(Merchant, Order));
        Assert.Equal(LegacyVersion(order), DeliveryChecklistRules.BuildIfood(order).Version);
        // O iFood mandar foto no payload não muda a versão de quem já conferiu.
        var withIfoodImage = Normalize(PayloadWithItem(i => i["imageUrl"] = "https://ifood/a.png"));
        Assert.Equal(LegacyVersion(order), DeliveryChecklistRules.BuildIfood(withIfoodImage).Version);
        Assert.Equal("https://ifood/a.png", DeliveryChecklistRules.BuildIfood(withIfoodImage).Items[0].ImageUrl);
    }

    [Fact]
    public void LinkedImageAloneKeepsTheVersionButTheBagWarningChangesItAndRequiresRecheck()
    {
        var order = Normalize(IfoodOrderImportTests.Payload(Merchant, Order));
        var baseline = DeliveryChecklistRules.BuildIfood(order);
        string Links(bool attention) => JsonSerializer.Serialize(new Dictionary<string, object> { ["item-external"] = new { produtoId = Guid.NewGuid(), imagemUrl = "https://loja/p.png", atencao = attention } });
        var linkedNoWarning = DeliveryChecklistRules.BuildIfood(IfoodOrderReadModel.ApplyLinks(order, Links(false)));
        Assert.Equal(baseline.Version, linkedNoWarning.Version);
        Assert.Equal("https://loja/p.png", linkedNoWarning.Items[0].ImageUrl);
        Assert.False(linkedNoWarning.Items[0].Extra);

        var warned = DeliveryChecklistRules.BuildIfood(IfoodOrderReadModel.ApplyLinks(order, Links(true)));
        Assert.NotEqual(baseline.Version, warned.Version);
        Assert.True(warned.Items[0].Extra);
        // A conferência feita antes do aviso deixa de valer (o app trata CHECKLIST_CHANGED), e o aviso exige reconferir o item.
        var oldConfirmation = new DeliveryChecklistConfirmation { PedidoId = 7, Version = baseline.Version, ConfirmedKeys = baseline.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() };
        Assert.Equal("CHECKLIST_CHANGED", Assert.Throws<DeliveryDomainException>(() => DeliveryChecklistRules.Validate(7, warned, oldConfirmation)).Code);
        var missingRecheck = new DeliveryChecklistConfirmation { PedidoId = 7, Version = warned.Version, ConfirmedKeys = warned.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() };
        Assert.Equal("CHECKLIST_INCOMPLETE", Assert.Throws<DeliveryDomainException>(() => DeliveryChecklistRules.Validate(7, warned, missingRecheck)).Code);
        missingRecheck.RecheckedExtraKeys = new() { warned.Items[0].Key };
        DeliveryChecklistRules.Validate(7, warned, missingRecheck);
    }
}
