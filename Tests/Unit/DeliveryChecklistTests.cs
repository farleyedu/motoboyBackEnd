using APIBack.DTOs.Delivery;
using APIBack.Service;
using Xunit;
namespace APIBack.Tests.Unit;

public sealed class DeliveryChecklistTests
{
    [Fact] public void QuantitiesOfExtrasFollowTheOrderedProductQuantity()
    {
        var result = DeliveryChecklistRules.Build(new[] { new PedidoItemDto { ItemId="combo-1",Nome="Combo",Quantidade=2,Adicionais=new() { new() { Nome="Suco",Quantidade=2 } } } });
        Assert.Equal(2,result.Items[0].Quantity);
        Assert.Equal(4,result.Items[1].Quantity);
        Assert.Equal("Combo",result.Items[1].ParentName);
    }
    [Fact] public void CheckingProductsWithoutRecheckingExtrasCannotComplete()
    {
        var manifest=DeliveryChecklistRules.Build(new[] { new PedidoItemDto { Nome="Refrigerante",Quantidade=2,AtencaoMotoboy=true } });
        var confirmation=new DeliveryChecklistConfirmation { PedidoId=87,Version=manifest.Version,ConfirmedKeys=manifest.Items.Select(i=>i.Key).ToList() };
        Assert.Throws<DeliveryDomainException>(()=>DeliveryChecklistRules.Validate(87,manifest,confirmation));
        confirmation.RecheckedExtraKeys=confirmation.ConfirmedKeys.ToList();
        DeliveryChecklistRules.Validate(87,manifest,confirmation);
        Assert.Throws<DeliveryDomainException>(()=>DeliveryChecklistRules.Validate(88,manifest,confirmation));
    }
    [Fact] public void NamesAndIngredientsDoNotAutomaticallyRequireAttention()
    {
        var manifest = DeliveryChecklistRules.Build(new[] { new PedidoItemDto { Nome="Refrigerante", Adicionais=new() { new() { Nome="Bacon" }, new() { Nome="Sacola separada", AtencaoMotoboy=true } } } });
        Assert.False(manifest.Items[0].Extra);
        Assert.False(manifest.Items[1].Extra);
        Assert.True(manifest.Items[2].Extra);
    }
    [Fact] public void LegacyJsonRetainsCatalogIdsForExplicitAttention()
    {
        var product = Guid.NewGuid(); var extra = Guid.NewGuid();
        var items = LegacyItemsParser.Parse($"[{{\"produtoId\":\"{product}\",\"nome\":\"Combo\",\"adicionais\":[{{\"id\":\"{extra}\",\"nome\":\"Bebida\"}}]}}]");
        Assert.Equal(product,items[0].ProdutoId); Assert.Equal(extra,items[0].Adicionais[0].Id);
    }
    [Fact] public void NoAttentionNeedsNoSecondConfirmationAndFlagChangesInvalidatePreviousManifest()
    {
        var item = new PedidoItemDto { Nome="Produto", Quantidade=1 };
        var manifest = DeliveryChecklistRules.Build(new[] { item });
        var confirmation = new DeliveryChecklistConfirmation { PedidoId=23, Version=manifest.Version, ConfirmedKeys=manifest.Items.Select(i=>i.Key).ToList() };
        DeliveryChecklistRules.Validate(23,manifest,confirmation);
        item.AtencaoMotoboy=true;
        var changed = DeliveryChecklistRules.Build(new[] { item });
        Assert.NotEqual(manifest.Version,changed.Version);
        Assert.Throws<DeliveryDomainException>(()=>DeliveryChecklistRules.Validate(23,changed,confirmation));
    }
    [Fact] public void UnknownContentsUseOneManualConfirmationWithoutInventingExtras()
    {
        var manifest = DeliveryChecklistRules.Build(Array.Empty<PedidoItemDto>());
        DeliveryChecklistRules.Validate(23,manifest,new() { PedidoId=23,Version=manifest.Version,ConfirmedKeys=new(){"manual"} });
    }
    [Fact] public void ChangedQuantityInvalidatesConfirmationButCatalogPhotoDoesNot()
    {
        var item=new PedidoItemDto { ItemId="item-1",Nome="Brownie",Quantidade=1,ImagemUrl="foto-a.jpg" };
        var initial=DeliveryChecklistRules.Build(new[] {item});
        item.ImagemUrl="foto-b.jpg";
        Assert.Equal(initial.Version,DeliveryChecklistRules.Build(new[]{item}).Version);
        item.Quantidade=2;
        Assert.NotEqual(initial.Version,DeliveryChecklistRules.Build(new[]{item}).Version);
    }
}
