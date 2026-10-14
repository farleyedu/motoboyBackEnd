using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Events;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class IfoodEventIngressTests
{
    private static readonly Guid Store = Guid.NewGuid(), Merchant = Guid.NewGuid(), Event = Guid.NewGuid();
    internal static IfoodEventEnvelope Envelope(Guid merchant, Guid? id = null, string code = "PLC", string? marker = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        { id = id ?? Guid.NewGuid(), merchantId = merchant, orderId = Guid.NewGuid(), code, createdAt = "2026-10-13T12:00:00Z", metadata = new { marker } }));
        return IfoodEventEnvelope.Parse(document.RootElement);
    }
    private static IfoodOptions PollingOptions()
    {
        var options = IfoodAuthenticationTests.EnabledOptions();
        options.PollingEnabled = true;
        options.PollingMerchants.Add(new() { MerchantId = Merchant, EstabelecimentoId = Store });
        return options;
    }
    private sealed class Factory(IfoodAuthenticationTests.Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false) { MaxResponseContentBufferSize = 4 * 1024 * 1024 }; }
    private static (IfoodEventsClient Client, Mock<IIfoodTokenProvider> Tokens) Client(IfoodAuthenticationTests.Handler handler, bool authorized = true)
    {
        var connections = new Mock<IIfoodConnectionRepository>();
        connections.Setup(c => c.IsAuthorizedAsync(Store, Merchant, It.IsAny<CancellationToken>())).ReturnsAsync(authorized);
        var tokens = new Mock<IIfoodTokenProvider>();
        tokens.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("private-test-token", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        var access = new IfoodMerchantAccess(connections.Object, tokens.Object, IfoodAuthenticationTests.Monitor(PollingOptions()));
        return (new(new Factory(handler), access, tokens.Object, new IfoodAuthenticationTests.Clock()), tokens);
    }
    private static Task BeforeSend(CancellationToken _) => Task.CompletedTask;

    [Fact]
    public async Task PollingUsesEventsEndpointMerchantFilterAndPreservesUnknownMetadata()
    {
        var incoming = Envelope(Merchant, Event, "FUTURE_EVENT", "private-client-data");
        var before = false;
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (request, _) =>
            {
                Assert.True(before);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://merchant-api.ifood.com.br/events/v1.0/events:polling", request.RequestUri!.ToString());
                Assert.Equal(Merchant.ToString(), request.Headers.GetValues("x-polling-merchants").Single());
                Assert.Equal("private-test-token", request.Headers.Authorization!.Parameter);
                Assert.DoesNotContain("excludeHeartbeat", request.RequestUri.Query);
                Assert.DoesNotContain("types", request.RequestUri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + incoming.PayloadJson + "]") });
            }
        };
        var events = await Client(handler).Client.PollAsync(PollingOptions().PollingMerchants, _ => { before = true; return Task.CompletedTask; }, default);
        Assert.Single(events);
        Assert.Equal("FUTURE_EVENT", events[0].Code);
        Assert.Contains("private-client-data", events[0].PayloadJson);
        Assert.DoesNotContain("private-client-data", events[0].ToString());
        Assert.DoesNotContain("private-client-data", JsonSerializer.Serialize(events[0]));
    }

    [Fact]
    public async Task EmptyPollingAndAckUseDistinctStatusContractsAndUniqueIds()
    {
        var handler = new IfoodAuthenticationTests.Handler { Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)) };
        var client = Client(handler).Client;
        Assert.Empty(await client.PollAsync(PollingOptions().PollingMerchants, BeforeSend, default));
        handler.Send = async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://merchant-api.ifood.com.br/events/v1.0/events/acknowledgment", request.RequestUri!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Single(body.RootElement.EnumerateArray());
            Assert.Equal(Event, body.RootElement[0].GetProperty("id").GetGuid());
            Assert.False(body.RootElement[0].TryGetProperty("merchantId", out var ignoredProperty));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };
        var receipt = new IfoodEventReceipt(Merchant, Store, Event);
        await client.AcknowledgeAsync(new[] { receipt, receipt }, BeforeSend, default);
    }

    [Theory]
    [InlineData(401, "IFOOD_EVENTS_UNAUTHORIZED")]
    [InlineData(403, "IFOOD_EVENTS_FORBIDDEN")]
    [InlineData(429, "IFOOD_EVENTS_RATE_LIMIT")]
    [InlineData(500, "IFOOD_EVENTS_REMOTE_ERROR")]
    [InlineData(302, "IFOOD_EVENTS_REMOTE_ERROR")]
    public async Task HttpFailuresAreSanitizedAnd401InvalidatesWithoutImmediateReplay(int status, string code)
    {
        var handler = new IfoodAuthenticationTests.Handler
        {
            Send = (_, _) =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-test-token") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(response);
            }
        };
        var (client, tokens) = Client(handler);
        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => client.PollAsync(PollingOptions().PollingMerchants, BeforeSend, default));
        Assert.Equal(code, ex.Code);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.DoesNotContain("private-test-token", ex.ToString());
        Assert.Equal(1, handler.Calls);
        tokens.Verify(t => t.Invalidate(It.IsAny<IfoodAccessToken>()), status == 401 ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    public async Task InvalidPollingShapeDoesNotInventEvents(string body)
    {
        var handler = new IfoodAuthenticationTests.Handler { Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }) };
        Assert.Equal("IFOOD_EVENTS_INVALID_RESPONSE", (await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).Client.PollAsync(PollingOptions().PollingMerchants, BeforeSend, default))).Code);
    }

    [Fact]
    public async Task UnauthorizedMerchantAndLostReservationPreventSendingHttp()
    {
        var handler = new IfoodAuthenticationTests.Handler();
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler, false).Client.PollAsync(PollingOptions().PollingMerchants, BeforeSend, default));
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => Client(handler).Client.PollAsync(PollingOptions().PollingMerchants,
            _ => throw new IfoodIntegrationException("IFOOD_POLLING_LEASE_LOST"), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"id\":\"invalid\",\"merchantId\":\"invalid\"}", false)]
    [InlineData("\"malformed-item\"", false)]
    [InlineData("{\"createdAt\":\"not-a-date\",\"code\":\"PLC\"}", true)]
    [InlineData("{\"code\":\"PLC\",\"orderId\":\"invalid\"}", true)]
    public void ParserRetainsMalformedElementsForDurableReview(string body, bool review)
    {
        using var json = JsonDocument.Parse(body);
        var item = IfoodEventEnvelope.Parse(json.RootElement);
        Assert.Equal(body, item.PayloadJson);
        Assert.Null(item.EventId);
        Assert.Null(item.MerchantId);
        if (review) Assert.True(item.RequiresReview);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task DisabledTransportNeverTouchesRepositoryOrHttp(bool enabled, bool polling)
    {
        var options = new IfoodOptions { Enabled = enabled, PollingEnabled = polling };
        var service = new IfoodPollingService(new Mock<IIfoodInboxRepository>(MockBehavior.Strict).Object,
            new Mock<IIfoodEventsClient>(MockBehavior.Strict).Object, IfoodAuthenticationTests.Monitor(options), new IfoodAuthenticationTests.Clock());
        Assert.Equal(TimeSpan.FromSeconds(30), await service.RunOnceAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServicePersistsBeforeAckAndDatabaseFailurePreventsAck(bool failPersistence)
    {
        var trace = new List<string>();
        var clock = new IfoodAuthenticationTests.Clock();
        var options = PollingOptions();
        var lease = new IfoodPollingLease(new string('a', 64), Guid.NewGuid(), clock.GetUtcNow().AddSeconds(30));
        var inbox = new Mock<IIfoodInboxRepository>();
        var events = new Mock<IIfoodEventsClient>();
        inbox.Setup(r => r.AcquirePollingAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        inbox.Setup(r => r.StartPollingRequestAsync(lease, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease.NextPollAtUtc);
        inbox.Setup(r => r.PersistAsync(lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<IReadOnlyList<IfoodEventEnvelope>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { trace.Add("commit"); return failPersistence ? Task.FromException(new IfoodIntegrationException("IFOOD_INBOX_DATABASE_ERROR")) : Task.CompletedTask; });
        var receipt = new IfoodEventReceipt(Merchant, Store, Event);
        inbox.SetupSequence(r => r.PendingAcknowledgmentsAsync(lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { receipt }).ReturnsAsync(Array.Empty<IfoodEventReceipt>());
        inbox.Setup(r => r.ReleasePollingAsync(lease, It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease.NextPollAtUtc);
        events.Setup(e => e.PollAsync(It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<IfoodPollingMerchant> _, Func<CancellationToken, Task> send, CancellationToken ct) => { await send(ct); trace.Add("poll"); return new[] { Envelope(Merchant, Event) }; });
        events.Setup(e => e.AcknowledgeAsync(It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<IfoodEventReceipt> _, Func<CancellationToken, Task> send, CancellationToken ct) => { await send(ct); trace.Add("ack"); });
        var service = new IfoodPollingService(inbox.Object, events.Object, IfoodAuthenticationTests.Monitor(options), clock);
        if (failPersistence) await Assert.ThrowsAsync<IfoodIntegrationException>(() => service.RunOnceAsync());
        else await service.RunOnceAsync();
        Assert.Equal(failPersistence ? new[] { "poll", "commit" } : new[] { "poll", "commit", "ack" }, trace);
        inbox.Verify(r => r.AcknowledgedAsync(lease, It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), It.IsAny<CancellationToken>()), failPersistence ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task LostAckResponseRemainsPendingAndFutureEmptyPollingCanRecoverIt()
    {
        var options = PollingOptions(); var clock = new IfoodAuthenticationTests.Clock();
        var lease = new IfoodPollingLease(new string('a', 64), Guid.NewGuid(), clock.GetUtcNow().AddSeconds(30));
        var receipt = new IfoodEventReceipt(Merchant, Store, Event);
        var inbox = new Mock<IIfoodInboxRepository>(); var events = new Mock<IIfoodEventsClient>();
        inbox.Setup(r => r.AcquirePollingAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        inbox.Setup(r => r.StartPollingRequestAsync(lease, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease.NextPollAtUtc);
        inbox.SetupSequence(r => r.PendingAcknowledgmentsAsync(lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { receipt }).ReturnsAsync(new[] { receipt }).ReturnsAsync(Array.Empty<IfoodEventReceipt>());
        inbox.Setup(r => r.ReleasePollingAsync(lease, It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(lease.NextPollAtUtc);
        events.Setup(e => e.PollAsync(It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<IfoodEventEnvelope>());
        events.SetupSequence(e => e.AcknowledgeAsync(It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IfoodIntegrationException("IFOOD_EVENTS_TIMEOUT", retryable: true)).Returns(Task.CompletedTask);
        var service = new IfoodPollingService(inbox.Object, events.Object, IfoodAuthenticationTests.Monitor(options), clock);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => service.RunOnceAsync());
        inbox.Verify(r => r.AcknowledgmentFailedAsync(lease, It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), "IFOOD_EVENTS_TIMEOUT", TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()), Times.Once());
        inbox.Verify(r => r.AcknowledgedAsync(lease, It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), It.IsAny<CancellationToken>()), Times.Never());
        await service.RunOnceAsync();
        inbox.Verify(r => r.AcknowledgedAsync(lease, It.IsAny<IReadOnlyList<IfoodEventReceipt>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void PollingConfigurationRejectsInvalidMappingsAndUnsafeLeaseDuration()
    {
        var validator = new IfoodOptionsValidator();
        var options = PollingOptions();
        Assert.True(validator.Validate(null, options).Succeeded);
        options.PollingMerchants.Add(options.PollingMerchants[0]);
        Assert.True(validator.Validate(null, options).Failed);
        options = PollingOptions(); options.PollingLeaseSeconds = options.HttpTimeoutSeconds;
        Assert.True(validator.Validate(null, options).Failed);
        options = PollingOptions(); options.AckBatchSize = 2001;
        Assert.True(validator.Validate(null, options).Failed);
    }
}
