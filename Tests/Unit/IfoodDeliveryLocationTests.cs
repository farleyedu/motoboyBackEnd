using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service.Interface;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodDeliveryLocationTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(91, -48, false)]
    [InlineData(-18, -181, false)]
    [InlineData(-18.92, -48.27, true)]
    public void RejectsInvalidAndPlaceholderPoints(double lat, double lon, bool valid) => Assert.Equal(valid, IfoodDeliveryLocation.ValidPoint(lat, lon));

    [Fact]
    public void LocalCorrectionPreservesOriginalAndOnlyResolvesLocationReview()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var root = JsonNode.Parse(IfoodOrderImportTests.Payload(merchant, id))!;
        root["delivery"]!["deliveryAddress"]!["coordinates"] = new JsonObject { ["latitude"] = 0, ["longitude"] = 0 };
        root["payments"]!["pending"] = null;
        var original = root.ToJsonString();
        var address = JsonNode.Parse("""{"streetName":"Rua Corrigida","streetNumber":"20","city":"Uberlândia","coordinates":{"latitude":-18.91,"longitude":-48.26}}""")!.AsObject();
        var payload = IfoodDeliveryLocation.Set(original, address, "manual", 7, "Confirmado com cliente");
        var normalized = IfoodOrderNormalizer.Normalize(payload, merchant, id);
        Assert.Equal("Rua Teste", normalized.OriginalAddress.GetProperty("streetName").GetString());
        Assert.Equal("Rua Corrigida", normalized.Address.GetProperty("streetName").GetString());
        Assert.DoesNotContain("IFOOD_DELIVERY_LOCATION_REQUIRED", normalized.ReviewReasons);
        Assert.Contains("IFOOD_PAYMENT_DATA_REVIEW", normalized.ReviewReasons);
        Assert.True(normalized.RequiresReview); Assert.Equal("manual", normalized.LocationSource);
        Assert.Equal(-18.91, normalized.Latitude);
        var refreshed = IfoodOrderNormalizer.Normalize(IfoodDeliveryLocation.Preserve(IfoodOrderImportTests.Payload(merchant, id), payload), merchant, id);
        Assert.Equal(-18.91, refreshed.Latitude); Assert.Equal("manual", refreshed.LocationSource);
        Assert.False(refreshed.RequiresReview); Assert.Equal(65m, refreshed.OrderAmount);
    }

    [Theory]
    [InlineData("found", true, true)]
    [InlineData("approximate", false, false)]
    [InlineData("not_found", false, false)]
    [InlineData("unavailable", false, false)]
    public async Task AutomaticLookupUsesOnlyExactResultAndKeepsFailureReason(string status, bool exact, bool resolved)
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var root = JsonNode.Parse(IfoodOrderImportTests.Payload(merchant, id))!;
        root["delivery"]!["deliveryAddress"]!["coordinates"] = null;
        var location = new Mock<ILocalizacaoService>();
        location.Setup(x => x.ConsultarEnderecoAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new GeocodeConsulta(status,
            status is "found" or "approximate" ? new("-18.91", "-48.26", exact, "Google") : null));
        using var services = new ServiceCollection().AddSingleton(location.Object).BuildServiceProvider();
        var payload = await IfoodDeliveryLocation.TryLocateAsync(root.ToJsonString(), merchant, id, services.GetRequiredService<IServiceScopeFactory>(), default);
        var order = IfoodOrderNormalizer.Normalize(payload, merchant, id);
        Assert.Equal(resolved, !order.ReviewReasons.Contains("IFOOD_DELIVERY_LOCATION_REQUIRED"));
        Assert.Equal(resolved ? "geocoded" : null, order.LocationSource);
        if (!resolved) Assert.Equal(status, JsonNode.Parse(payload)![IfoodDeliveryLocation.Key]!["lookupStatus"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Null, order.OriginalAddress.GetProperty("coordinates").ValueKind);
    }

    [Fact]
    public void AutomaticPointIsReevaluatedWhenOfficialAddressChanges()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = IfoodOrderImportTests.Payload(merchant, id);
        var address = JsonNode.Parse(original)!["delivery"]!["deliveryAddress"]!.AsObject();
        var previous = IfoodDeliveryLocation.Set(original, address, "geocoded", null, "Busca exata");
        var incoming = JsonNode.Parse(original)!;
        incoming["delivery"]!["deliveryAddress"]!["streetName"] = "Nova rua";
        var result = IfoodOrderNormalizer.Normalize(IfoodDeliveryLocation.Preserve(incoming.ToJsonString(), previous), merchant, id);
        Assert.Null(result.LocationSource); Assert.Equal("Nova rua", result.Address.GetProperty("streetName").GetString());
    }
}
