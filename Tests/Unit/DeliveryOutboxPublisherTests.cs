using APIBack.Hubs;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

public class DeliveryOutboxPublisherTests
{
    [Fact]
    public async Task FailedSendKeepsEventPendingAndPreservesOrderForSameMotoboy()
    {
        var first = Record(1); var next = Record(1); var other = Record(2);
        var (publisher, repo, proxy) = Create(new[] { first, next, other });
        var calls = 0;
        proxy.Setup(p => p.SendCoreAsync("event", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? Task.FromException(new IOException("offline")) : Task.CompletedTask);
        await publisher.PublishBatchAsync(default);
        Assert.Equal(2, calls);
        repo.Verify(r => r.CompleteAsync(It.IsAny<Guid>(), first.EventId, "offline", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.CompleteAsync(It.IsAny<Guid>(), next.EventId, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.CompleteAsync(It.IsAny<Guid>(), other.EventId, null, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LostLeaseStopsPublishingRemainingEvents()
    {
        var records = new[] { Record(1), Record(2) }; var (publisher, repo, proxy) = Create(records);
        repo.Setup(r => r.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await publisher.PublishBatchAsync(default);
        proxy.Verify(p => p.SendCoreAsync("event", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShutdownDuringSendDoesNotAcknowledgeUndeliveredEvent()
    {
        using var stop = new CancellationTokenSource();
        var (publisher, repo, proxy) = Create(new[] { Record(1) });
        proxy.Setup(p => p.SendCoreAsync("event", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(() => { stop.Cancel(); return Task.FromCanceled(stop.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishBatchAsync(stop.Token));
        repo.Verify(r => r.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.ReleaseAsync(It.IsAny<Guid>(), It.Is<CancellationToken>(c => !c.IsCancellationRequested)), Times.Once);
    }

    private static DeliveryOutboxRecord Record(int motoboy) => new() { EventId = Guid.NewGuid(), EventName = "event",
        TargetGroup = "store", MotoboyId = motoboy, Payload = "{}", OccurredAtUtc = DateTimeOffset.UtcNow };
    private static (DeliveryOutboxPublisher, Mock<IDeliveryOutboxRepository>, Mock<IClientProxy>) Create(DeliveryOutboxRecord[] records)
    {
        var repo = new Mock<IDeliveryOutboxRepository>(); var hub = new Mock<IHubContext<DeliveryHub>>();
        var clients = new Mock<IHubClients>(); var proxy = new Mock<IClientProxy>();
        repo.Setup(r => r.ClaimAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(records);
        repo.Setup(r => r.CompleteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hub.SetupGet(h => h.Clients).Returns(clients.Object); clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return (new DeliveryOutboxPublisher(repo.Object, hub.Object, Microsoft.Extensions.Options.Options.Create(new DeliveryTrackingOptions()),
            NullLogger<DeliveryOutboxPublisher>.Instance), repo, proxy);
    }
}
