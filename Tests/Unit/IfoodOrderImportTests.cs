using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Orders;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodOrderImportTests
{
    internal static string Payload(Guid merchant, Guid order, string type = "DELIVERY", string carrier = "MERCHANT")
    {
        var root = JsonNode.Parse("""
{
 "displayId":"001234","orderType":"DELIVERY","orderTiming":"IMMEDIATE","category":"FOOD","createdAt":"2026-10-13T18:00:00Z","isTest":true,
 "merchant":{},"customer":{"name":"Cliente de teste","documentNumber":"test-only-document","segmentation":"private-segment","phone":{"number":"08001234567","localizer":"009876","localizerExpiration":"2026-10-13T22:00:00Z"}},
 "total":{"subTotal":80,"deliveryFee":10,"additionalFees":2,"benefits":27,"orderAmount":65},
 "payments":{"prepaid":20,"pending":45,"methods":[{"value":20,"type":"ONLINE","method":"PIX","currency":"BRL"},{"value":45,"type":"OFFLINE","method":"CASH","currency":"BRL","cash":{"changeFor":50}}]},
 "items":[{"id":"item-external","name":"Produto de teste","quantity":1.5,"unit":"KG","unitPrice":10.125,"price":15.19,"optionsPrice":5.06,"totalPrice":21,"observations":"Sem cebola","options":[{"id":"option-external","name":"Extra","quantity":2.5,"unitPrice":2.024,"price":5.06}]}],
 "benefits":[{"value":27,"sponsorshipValues":[{"name":"IFOOD","value":20},{"name":"MERCHANT","value":7}]}],
 "delivery":{"deliveredBy":"MERCHANT","pickupCode":"private-pickup-code","deliveryAddress":{"formattedAddress":"Rua Teste, 10","streetName":"Rua Teste","streetNumber":"10","neighborhood":"Centro","city":"Uberlandia","state":"MG","postalCode":"38400000","coordinates":{"latitude":-18.92,"longitude":-48.27}}},
 "extraInfo":"Conferir a sacola","unknownExtension":{"preserved":true}
}
""")!.AsObject();
        root["id"] = order.ToString(); root["merchant"]!["id"] = merchant.ToString(); root["orderType"] = type;
        root["delivery"]!["deliveredBy"] = carrier;
        if (type != "DELIVERY") root.Remove("delivery");
        return root.ToJsonString();
    }

    [Fact]
    public void PreservesOfficialTotalsFractionalItemsOptionsAndPartialPaymentWithoutRepricing()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var order = IfoodOrderNormalizer.Normalize(Payload(merchant, id), merchant, id);
        Assert.Equal("001234", order.DisplayNumber); Assert.Equal(65m, order.OrderAmount);
        Assert.NotEqual(order.OrderAmount, order.Subtotal + order.DeliveryFee);
        Assert.Equal(45m, order.AmountToCollect); Assert.Equal("parcial", order.PaymentState);
        Assert.False(order.RequiresReview);
        Assert.Equal(1.5m, order.Items[0].Quantity); Assert.Equal(10.125m, order.Items[0].UnitPrice);
        Assert.Equal(21m, order.Items[0].TotalPrice);
        Assert.Equal(2.5m, order.Items[0].Options[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("CASH", order.Payments[1].Method); Assert.Equal(50m, order.Payments[1].ChangeFor);
        Assert.Equal(-18.92, order.Latitude); Assert.Contains("unknownExtension", order.PayloadJson);
    }

    [Theory]
    [InlineData("TAKEOUT", false)]
    [InlineData("DINE_IN", false)]
    [InlineData("FUTURE_MODE", true)]
    public void NonDeliveryDoesNotRequireAddressAndUnknownModalityNeedsReview(string type, bool review)
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var result = IfoodOrderNormalizer.Normalize(Payload(merchant, id, type), merchant, id);
        Assert.Equal(type, result.OrderType); Assert.Equal(JsonValueKind.Null, result.Address.ValueKind);
        Assert.Equal(review, result.RequiresReview);
    }

    [Fact]
    public void RemovesFiscalCustomerDataAndExpectedCodesBeforeCacheOrPublicSerialization()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var result = IfoodOrderNormalizer.Normalize(Payload(merchant, id), merchant, id);
        Assert.DoesNotContain("test-only-document", result.PayloadJson); Assert.DoesNotContain("private-segment", result.PayloadJson);
        Assert.DoesNotContain("private-pickup-code", result.PayloadJson);
        var visible = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("PayloadJson", visible); Assert.DoesNotContain("private-pickup-code", visible);
        Assert.Equal("08001234567", result.ContactNumber); Assert.Equal("009876", result.Localizer);
        Assert.DoesNotContain("Cliente de teste", result.ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("9999999999999999999999999999999999")]
    public void InvalidTotalNeverBecomesRoundedOrZero(string amount)
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var json = Payload(merchant, id).Replace("\"orderAmount\":65", "\"orderAmount\":" + amount);
        Assert.Throws<IfoodIntegrationException>(() => IfoodOrderNormalizer.Normalize(json, merchant, id));
    }

    [Fact]
    public void MissingPaymentUnknownCurrencyOrMethodNeverBecomesCashOrPaid()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        var root = JsonNode.Parse(Payload(merchant, id))!;
        root["payments"]!["pending"] = null;
        var result = IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id);
        Assert.Equal("requer_revisao", result.PaymentState); Assert.Null(result.AmountToCollect);
        root = JsonNode.Parse(Payload(merchant, id))!;
        root["payments"]!["methods"]![1]!["method"] = "FUTURE_METHOD";
        result = IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id);
        Assert.Equal("FUTURE_METHOD", result.Payments[1].Method); Assert.Null(result.AmountToCollect);
        root["payments"]!["methods"]![1]!["currency"] = "UNKNOWN";
        Assert.Null(IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id).AmountToCollect);
    }

    [Fact]
    public void ZeroPendingIsDistinctFromMissingAndBenefitsDoNotDefineCharge()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid(); var root = JsonNode.Parse(Payload(merchant, id))!;
        root["payments"]!["pending"] = 0; root["payments"]!["prepaid"] = 65;
        root["payments"]!["methods"]![0]!["value"] = 65; root["payments"]!["methods"]!.AsArray().RemoveAt(1);
        root["total"]!["benefits"] = 100;
        var result = IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id);
        Assert.Equal(65m, result.OrderAmount); Assert.Equal(0m, result.AmountToCollect); Assert.Equal("pago", result.PaymentState);
    }

    [Fact]
    public void IdentityMismatchIncompleteScheduleAndMissingItemQuantityCannotBeInvented()
    {
        var merchant = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.Equal("IFOOD_ORDER_IDENTITY_MISMATCH", Assert.Throws<IfoodIntegrationException>(() => IfoodOrderNormalizer.Normalize(Payload(merchant, id), Guid.NewGuid(), id)).Code);
        var root = JsonNode.Parse(Payload(merchant, id))!; root["orderTiming"] = "SCHEDULED";
        root["schedule"] = new JsonObject { ["deliveryDateTimeStart"] = "2026-10-14T20:00:00Z" };
        Assert.True(IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id).RequiresReview);
        root["items"]![0]!.AsObject().Remove("quantity");
        Assert.Throws<IfoodIntegrationException>(() => IfoodOrderNormalizer.Normalize(root.ToJsonString(), merchant, id));
    }

    [Theory]
    [InlineData(401, "IFOOD_ORDER_UNAUTHORIZED", true)]
    [InlineData(403, "IFOOD_ORDER_FORBIDDEN", false)]
    [InlineData(404, "IFOOD_ORDER_NOT_FOUND", true)]
    [InlineData(429, "IFOOD_ORDER_RATE_LIMIT", true)]
    [InlineData(500, "IFOOD_ORDER_REMOTE_ERROR", true)]
    public async Task OrderClientClassifiesFailuresWithoutLeakingBodyOrImmediatelyReplaying(int status, string code, bool retryable)
    {
        var merchant = Guid.NewGuid(); var store = Guid.NewGuid(); var id = Guid.NewGuid();
        var connections = new Mock<IIfoodConnectionRepository>(); connections.Setup(c => c.IsAuthorizedAsync(store, merchant, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var tokens = new Mock<IIfoodTokenProvider>(); tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("test-only-token", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        var access = new IfoodMerchantAccess(connections.Object, tokens.Object, IfoodAuthenticationTests.Monitor(IfoodAuthenticationTests.EnabledOptions()));
        var handler = new IfoodAuthenticationTests.Handler { Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-server-body") }) };
        var client = new IfoodOrderDetailsClient(new Factory(handler), access, tokens.Object);
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => client.GetAsync(new(merchant, id, store, Guid.NewGuid(), null), _ => Task.CompletedTask, default));
        Assert.Equal(code, ex.Code); Assert.Equal(retryable, ex.Retryable); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("private-server-body", ex.ToString());
        tokens.Verify(t => t.Invalidate(It.IsAny<IfoodAccessToken>()), status == 401 ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task DisabledImporterRequiresNoDatabaseOrHttp()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=9;Database=unused;Username=unused");
        var details = new Mock<IIfoodOrderDetailsClient>(MockBehavior.Strict);
        var service = new IfoodOrderImportService(new(source), new(source), details.Object, IfoodAuthenticationTests.Monitor(new IfoodOptions()));
        Assert.Equal(0, await service.RunOnceAsync());
    }
    internal sealed class Factory(IfoodAuthenticationTests.Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { MaxResponseContentBufferSize = 4 * 1024 * 1024 }; }
}
