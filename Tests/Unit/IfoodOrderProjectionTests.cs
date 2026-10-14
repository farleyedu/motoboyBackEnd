using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Events;
using APIBack.Integrations.Ifood.Orders;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodOrderProjectionTests
{
    internal static JsonElement Patch() => JsonSerializer.SerializeToElement(new
    {
        changes=new[] { new { changeType="ADD_ITEMS",items=new[] { new { id="second-item",name="Outro produto",quantity=0.25m,unit="KG",unitPrice=20m,totalPrice=5m,options=Array.Empty<object>() } } } },
        total=new { orderAmount=new { from=65m,to=70m } },
        payments=new { prepaid=20m,pending=50m,methods=new[] { new { value=20m,type="ONLINE",method="PIX",currency="BRL" },new { value=50m,type="OFFLINE",method="CASH",currency="BRL" } } }
    });
    [Theory]
    [InlineData("CFM","CONFIRMED")]
    [InlineData("CAN","CANCELLED")]
    [InlineData("OPA","ORDER_PATCHED")]
    [InlineData("HSS","HANDSHAKE_SETTLEMENT")]
    [InlineData("FUTURE","FUTURE")]
    public void PreservesUnknownCodesAndChecksIdentityFullCodeAndOfficialTimestamp(string code,string full)
    {
        var item=new IfoodInboxReservation { EventId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),ExternalOrderId=Guid.NewGuid(),EventCode=code };
        item.PayloadJson=JsonSerializer.Serialize(new { id=item.EventId,merchantId=item.MerchantId,orderId=item.ExternalOrderId,code,fullCode=full,createdAt="2026-10-13T19:00:00Z" });
        Assert.Equal(full,IfoodOrderProjectionEvent.Parse(item).Code);
        item.PayloadJson=item.PayloadJson.Replace(item.MerchantId.ToString(),Guid.NewGuid().ToString());
        Assert.Equal("IFOOD_EVENT_INVALID_IDENTITY",Assert.Throws<IfoodIntegrationException>(()=>IfoodOrderProjectionEvent.Parse(item)).Code);
    }
    [Theory]
    [InlineData("2026-10-13T19:00:00","CONFIRMED","IFOOD_EVENT_MISSING_TIMESTAMP")]
    [InlineData("2026-10-13T19:00:00Z","CANCELLED","IFOOD_EVENT_CODE_CONFLICT")]
    public void AmbiguousTimestampOrContradictoryCodeCannotDriveState(string date,string full,string error)
    {
        var item=new IfoodInboxReservation { EventId=Guid.NewGuid(),MerchantId=Guid.NewGuid(),ExternalOrderId=Guid.NewGuid(),EventCode="CFM" };
        item.PayloadJson=JsonSerializer.Serialize(new { id=item.EventId,merchantId=item.MerchantId,orderId=item.ExternalOrderId,code="CFM",fullCode=full,createdAt=date });
        Assert.Equal(error,Assert.Throws<IfoodIntegrationException>(()=>IfoodOrderProjectionEvent.Parse(item)).Code);
    }
    [Theory]
    [InlineData(-1,"ignored")]
    [InlineData(0,"review")]
    [InlineData(1,"review")]
    public void OlderEventsAndNewerRegressionsNeverRevertTerminalState(int offset,string expected)
    {
        var at=DateTimeOffset.Parse("2026-10-13T19:00:00Z");
        Assert.Equal(expected,IfoodProjectionOrdering.Decide(at.AddMinutes(offset),at,"CANCELLED","CONFIRMED",IfoodProjectionOrdering.StatusRank));
    }
    [Fact]
    public void PatchAppliesOfficialMoneyAndFractionalItemsWithoutCatalogOrRecalculation()
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();
        var result=IfoodOrderPatch.Apply(IfoodOrderImportTests.Payload(merchant,order),Patch(),merchant,order);
        Assert.True(result.Complete);Assert.Equal(70m,result.Order.OrderAmount);Assert.Equal(50m,result.Order.PendingAmount);
        Assert.Equal(0.25m,result.Order.Items[1].Quantity);Assert.Equal(5m,result.Order.Items[1].TotalPrice);
        Assert.DoesNotContain("private-pickup-code",result.Order.PayloadJson);
    }
    [Fact]
    public void PatchWithOnlySubtotalAndMethodsDoesNotInventOrderTotalOrCollectionBalance()
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();
        var patch=JsonNode.Parse(Patch().GetRawText())!.AsObject();
        patch["total"]=JsonNode.Parse("{\"subtotal\":{\"from\":80,\"to\":85}}");
        patch["payments"]!.AsObject().Remove("pending");patch["payments"]!.AsObject().Remove("prepaid");
        var result=IfoodOrderPatch.Apply(IfoodOrderImportTests.Payload(merchant,order),JsonSerializer.SerializeToElement(patch),merchant,order);
        Assert.False(result.Complete);Assert.True(result.Order.RequiresReview);Assert.Null(result.Order.AmountToCollect);Assert.Equal(85m,result.Order.Subtotal);
    }
    [Theory]
    [InlineData("old_total")]
    [InlineData("item_without_name")]
    [InlineData("unknown_change")]
    public void UnsupportedOrInconsistentPatchDoesNotMutatePreviousSnapshot(string problem)
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();var previous=IfoodOrderImportTests.Payload(merchant,order);
        var patch=JsonNode.Parse(Patch().GetRawText())!;
        if(problem=="old_total")patch["total"]!["orderAmount"]!["from"]=66m;
        if(problem=="item_without_name")patch["changes"]![0]!["items"]![0]!.AsObject().Remove("name");
        if(problem=="unknown_change")patch["changes"]![0]!["changeType"]="FUTURE";
        Assert.Throws<IfoodIntegrationException>(()=>IfoodOrderPatch.Apply(previous,JsonSerializer.SerializeToElement(patch),merchant,order));
        Assert.Equal(65m,IfoodOrderNormalizer.Normalize(previous,merchant,order).OrderAmount);
    }
    [Fact]
    public void DeleteAndEditPatchUseExternalIdentityAndFromValues()
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();
        var added=IfoodOrderPatch.Apply(IfoodOrderImportTests.Payload(merchant,order),Patch(),merchant,order).Order.PayloadJson;
        var edit=JsonSerializer.SerializeToElement(new { changes=new[] { new { changeType="EDIT_ITEMS",items=new[] { new { id="second-item",changes=new { quantity=new { from=0.25m,to=0.5m },totalPrice=new { from=5m,to=7m } } } } } } });
        var result=IfoodOrderPatch.Apply(added,edit,merchant,order);
        Assert.Equal(0.5m,result.Order.Items[1].Quantity);Assert.Equal(7m,result.Order.Items[1].TotalPrice);Assert.False(result.Complete);
        var delete=JsonSerializer.SerializeToElement(new { changes=new[] { new { changeType="DELETE_ITEMS",items=new[] { new { id="second-item" } } } } });
        Assert.Single(IfoodOrderPatch.Apply(added,delete,merchant,order).Order.Items);
    }
    [Fact]
    public void EditsOptionsWithOfficialFromToAndRejectsNullChangeWithoutInternalException()
    {
        var merchant=Guid.NewGuid();var order=Guid.NewGuid();var previous=IfoodOrderImportTests.Payload(merchant,order);
        var patch=JsonSerializer.SerializeToElement(new { changes=new[] { new { changeType="EDIT_ITEMS",items=new[] { new
        {
            id="item-external",changes=new { totalPrice=new { from=21m,to=22m },optionsPrice=new { from=5.06m,to=6.06m },
                optionsChanges=new[] { new { changeType="EDIT_OPTIONS",options=new[] { new { id="option-external",changes=new { quantity=new { from=2.5m,to=3.5m },price=new { from=5.06m,to=6.06m } } } } } } }
        } } } } });
        var result=IfoodOrderPatch.Apply(previous,patch,merchant,order);
        Assert.Equal(3.5m,result.Order.Items[0].Options[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(22m,result.Order.Items[0].TotalPrice);Assert.False(result.Complete);
        using var malformed=JsonDocument.Parse("{\"changes\":[null]}");
        Assert.Equal("IFOOD_PATCH_REQUIRES_RECONCILIATION",Assert.Throws<IfoodIntegrationException>(()=>IfoodOrderPatch.Apply(previous,malformed.RootElement,merchant,order)).Code);
    }
    [Fact]
    public async Task DisabledProjectionDoesNotOpenDatabaseAndInvalidEnabledLimitsAreRejected()
    {
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var options=IfoodAuthenticationTests.EnabledOptions();
        var service=new IfoodOrderProjectionService(new(source),new(source),IfoodAuthenticationTests.Monitor(options));
        Assert.Equal(0,await service.RunOnceAsync());
        options.ProjectionEnabled=true;options.ProjectionLeaseSeconds=1;
        Assert.True(new IfoodOptionsValidator().Validate(null,options).Failed);
    }
}
