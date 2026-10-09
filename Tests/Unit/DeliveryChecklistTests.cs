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
        var manifest=DeliveryChecklistRules.Build(new[] { new PedidoItemDto { Nome="Refrigerante",Quantidade=2 } });
        var confirmation=new DeliveryChecklistConfirmation { PedidoId=87,Version=manifest.Version,ConfirmedKeys=manifest.Items.Select(i=>i.Key).ToList() };
        Assert.Throws<DeliveryDomainException>(()=>DeliveryChecklistRules.Validate(87,manifest,confirmation));
        confirmation.RecheckedExtraKeys=confirmation.ConfirmedKeys.ToList();
        DeliveryChecklistRules.Validate(87,manifest,confirmation);
        Assert.Throws<DeliveryDomainException>(()=>DeliveryChecklistRules.Validate(88,manifest,confirmation));
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
