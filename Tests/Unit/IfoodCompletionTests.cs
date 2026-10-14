using System.Net;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Completion;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Repository;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodCompletionTests
{
    private sealed class Factory(IfoodAuthenticationTests.Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { MaxResponseContentBufferSize = 32768 }; }
    private static IfoodDeliveryCodeClient Client(IfoodAuthenticationTests.Handler handler, Guid merchant, Guid store)
    {
        var connections = new Mock<IIfoodConnectionRepository>();
        connections.Setup(c => c.IsAuthorizedAsync(store, merchant, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var tokens = new Mock<IIfoodTokenProvider>();
        tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("test-token", DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(10)));
        return new(new Factory(handler), new IfoodMerchantAccess(connections.Object, tokens.Object, IfoodAuthenticationTests.Monitor()), tokens.Object);
    }
    [Theory]
    [InlineData(200, "{\"valid\":true}", "verified")]
    [InlineData(200, "{\"valid\":false}", "rejected")]
    [InlineData(200, "{}", "uncertain")]
    [InlineData(200, "not-json", "uncertain")]
    [InlineData(202, "{}", "uncertain")]
    [InlineData(500, "{}", "uncertain")]
    [InlineData(404, "{}", "uncertain")]
    [InlineData(409, "{}", "uncertain")]
    [InlineData(429, "{}", "uncertain")]
    [InlineData(422, "{}", "review")]
    [InlineData(412, "{}", "rejected")]
    [InlineData(401, "{}", "rejected")]
    public async Task RemoteCodeUsesOrderEndpointAndOnlyExplicitTrueIsConfirmed(int status, string response, string expected)
    {
        var merchant = Guid.NewGuid(); var store = Guid.NewGuid(); var order = Guid.NewGuid(); var marked = false;
        var handler = new IfoodAuthenticationTests.Handler { Send = async (request, ct) =>
        {
            Assert.True(marked); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://merchant-api.ifood.com.br/order/v1.0/orders/{order:D}/verifyDeliveryCode", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("{\"code\":\"client-test-code\"}", await request.Content!.ReadAsStringAsync(ct));
            return new((HttpStatusCode)status) { Content = new StringContent(response) };
        } };
        var result = await Client(handler, merchant, store).SendAsync(store, merchant, order, "client-test-code", _ => { marked = true; return Task.CompletedTask; }, default);
        Assert.Equal(expected, result.State); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task BeforeSendFailureNeverIssuesHttpAndTransportFailureIsUncertainAndSanitized()
    {
        var merchant = Guid.NewGuid(); var store = Guid.NewGuid(); var handler = new IfoodAuthenticationTests.Handler(); var client = Client(handler, merchant, store);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => client.SendAsync(store, merchant, Guid.NewGuid(), "code", _ => throw new IfoodIntegrationException("LEASE_LOST"), default));
        Assert.Equal(0, handler.Calls);
        handler.Send = (_, _) => throw new HttpRequestException("secret-test-only");
        var result = await client.SendAsync(store, merchant, Guid.NewGuid(), "code", _ => Task.CompletedTask, default);
        Assert.Equal("uncertain", result.State); Assert.DoesNotContain("secret-test-only", result.ToString());
    }
    [Fact]
    public void CapabilityRequiresSeparateSwitchAndMerchantStorePair()
    {
        var merchant = Guid.NewGuid(); var store = Guid.NewGuid(); var options = IfoodCommandTests.Options(merchant, store);
        Assert.Equal("IFOOD_COMPLETION_DISABLED", PedidoQueueRepository.IfoodCompletionCapability(options, store, merchant));
        options.CompletionEnabled = true; Assert.Null(PedidoQueueRepository.IfoodCompletionCapability(options, store, merchant));
        Assert.NotNull(PedidoQueueRepository.IfoodCompletionCapability(options, Guid.NewGuid(), merchant));
        options.CommandsEnabled = false; Assert.NotNull(PedidoQueueRepository.IfoodCompletionCapability(options, store, merchant));
    }
    [Fact]
    public void CodeIsBoundToOperationAndFingerprintCannotExposeShortCodeOrCollideAcrossPurpose()
    {
        var options = IfoodAuthenticationTests.EnabledOptions(); var protector = new IfoodSecretProtector(Microsoft.Extensions.Options.Options.Create(options));
        var encrypted = protector.Protect("1234", "completion_a"); Assert.DoesNotContain("1234", encrypted);
        Assert.Equal("1234", protector.Unprotect(encrypted, "completion_a"));
        Assert.Throws<IfoodIntegrationException>(() => protector.Unprotect(encrypted, "completion_b"));
        Assert.NotEqual(protector.Fingerprint("1234", "completion_a"), protector.Fingerprint("1234", "completion_b"));
        Assert.NotEqual(protector.Fingerprint("1234", "completion_a"), protector.Fingerprint("5678", "completion_a"));
    }
    [Fact]
    public void OfficialPaymentCannotSwitchCashToPixAndUnknownMethodsRequireReview()
    {
        var merchant = Guid.NewGuid(); var orderId = Guid.NewGuid();
        var order = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(merchant, orderId), merchant, orderId);
        var request = new DeliveryCompletionRequest { Payments = new() { new() { Method = "pix", Amount = 45, ReceivedConfirmed = true } } };
        Assert.Equal("IFOOD_PAYMENT_METHOD_MISMATCH", Assert.Throws<DeliveryDomainException>(() => PedidoQueueRepository.ValidateOfficialPayments(request, order)).Code);
        request.Payments[0].Method = "dinheiro"; PedidoQueueRepository.ValidateOfficialPayments(request, order);
        var unknown = IfoodOrderNormalizer.Normalize(IfoodOrderImportTests.Payload(merchant, orderId).Replace("\"CASH\"", "\"UNKNOWN\""), merchant, orderId);
        Assert.Equal("IFOOD_PAYMENT_METHOD_REVIEW_REQUIRED", Assert.Throws<DeliveryDomainException>(() => PedidoQueueRepository.ValidateOfficialPayments(request, unknown)).Code);
    }
}
