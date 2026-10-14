using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodRoutingChecklistTests
{
    [Fact]
    public void OfficialChecklistPreservesFractionalUnitsAndOptionsAndRequiresCompleteConfirmation()
    {
        var merchant = Guid.NewGuid(); var order = Guid.NewGuid();
        var imported = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(merchant, order), merchant, order);
        var manifest = DeliveryChecklistRules.BuildIfood(imported);
        Assert.Equal(1.5m, manifest.Items[0].Quantity); Assert.Equal("KG", manifest.Items[0].Unit);
        Assert.Equal(2.5m, manifest.Items[1].Quantity); Assert.Equal("Produto de teste", manifest.Items[1].ParentName);
        Assert.Throws<DeliveryDomainException>(() => DeliveryChecklistRules.Validate(100, manifest, null));
        DeliveryChecklistRules.Validate(100, manifest, new() { PedidoId = 100, Version = manifest.Version,
            ConfirmedKeys = manifest.Items.Select(i => i.Key).ToList(), RecheckedExtraKeys = new() });
        var changed = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(merchant, order).Replace("1.5", "1.75"), merchant, order);
        Assert.NotEqual(manifest.Version, DeliveryChecklistRules.BuildIfood(changed).Version);
    }
}
