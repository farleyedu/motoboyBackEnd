using System.Net;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Connections;
using APIBack.Integrations.Ifood.Events;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

// Um merchant recusado pelo iFood (403) jamais pode interromper a leitura dos demais.
public sealed class IfoodMerchantIsolationTests
{
    private static readonly Guid StoreA = Guid.NewGuid(), StoreB = Guid.NewGuid(), MerchantA = Guid.NewGuid(), MerchantB = Guid.NewGuid();

    private sealed class Rig
    {
        public readonly IfoodAuthenticationTests.Clock Clock = new();
        public readonly Mock<IIfoodInboxRepository> Inbox = new();
        public readonly Mock<IIfoodEventsClient> Events = new();
        public readonly Mock<IIfoodConnectionHealth> Health = new();
        public readonly IfoodPollingLease Lease;
        public readonly List<IReadOnlyList<IfoodPollingMerchant>> Persisted = new();
        public readonly List<IReadOnlyList<IfoodEventEnvelope>> PersistedEvents = new();
        public readonly List<string> Polled = new();
        public Rig(params (Guid Merchant, Guid Store)[] merchants)
        {
            Lease = new IfoodPollingLease(new string('a', 64), Guid.NewGuid(), Clock.GetUtcNow().AddSeconds(30));
            Inbox.Setup(r => r.AcquirePollingAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Lease);
            Inbox.Setup(r => r.StartPollingRequestAsync(Lease, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Lease.NextPollAtUtc);
            Inbox.Setup(r => r.ReleasePollingAsync(Lease, It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Lease.NextPollAtUtc);
            Inbox.Setup(r => r.PersistAsync(Lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<IReadOnlyList<IfoodEventEnvelope>>(), It.IsAny<CancellationToken>()))
                .Returns((IfoodPollingLease _, IReadOnlyList<IfoodPollingMerchant> m, IReadOnlyList<IfoodEventEnvelope> e, CancellationToken _) =>
                { Persisted.Add(m.ToArray()); PersistedEvents.Add(e.ToArray()); return Task.CompletedTask; });
            Inbox.Setup(r => r.PendingAcknowledgmentsAsync(Lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<IfoodEventReceipt>());
            Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<Guid, DateTimeOffset>());
            Options = IfoodAuthenticationTests.EnabledOptions();
            Options.PollingEnabled = true;
            foreach (var (merchant, store) in merchants) Options.PollingMerchants.Add(new() { MerchantId = merchant, EstabelecimentoId = store });
        }
        public IfoodOptions Options { get; }
        public IfoodPollingService Service => new(Inbox.Object, Events.Object, IfoodAuthenticationTests.Monitor(Options), Clock, Health.Object);

        // Responde ao polling: merchants da lista que estao em "forbidden" derrubam a chamada inteira, como o iFood faz.
        public void Poll(ISet<Guid> forbidden, Func<Guid, IfoodEventEnvelope[]>? eventsFor = null)
            => Events.Setup(e => e.PollAsync(It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IReadOnlyList<IfoodPollingMerchant> merchants, Func<CancellationToken, Task> send, CancellationToken ct) =>
                {
                    await send(ct);
                    Polled.Add(string.Join('+', merchants.Select(m => m.MerchantId == MerchantA ? "A" : "B")));
                    if (merchants.Any(m => forbidden.Contains(m.MerchantId)))
                        throw new IfoodIntegrationException("IFOOD_EVENTS_FORBIDDEN", HttpStatusCode.Forbidden);
                    return merchants.SelectMany(m => eventsFor?.Invoke(m.MerchantId) ?? Array.Empty<IfoodEventEnvelope>()).ToArray();
                });
    }

    [Fact]
    public async Task OneForbiddenMerchantIsQuarantinedAndTheOtherStillReceivesEvents()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        var order = IfoodEventIngressTests.Envelope(MerchantA);
        rig.Poll(new HashSet<Guid> { MerchantB }, merchant => merchant == MerchantA ? new[] { order } : Array.Empty<IfoodEventEnvelope>());

        await rig.Service.RunOnceAsync();   // nao lanca: o 403 de B nao derruba o ciclo

        Assert.Equal(new[] { "A+B", "A", "B" }, rig.Polled);       // junto, depois um a um
        rig.Health.Verify(h => h.RecordPollFailureAsync(MerchantB, "IFOOD_EVENTS_FORBIDDEN", It.IsAny<CancellationToken>()), Times.Once());
        rig.Health.Verify(h => h.RecordPollFailureAsync(MerchantA, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        Assert.Equal(new[] { MerchantA }, rig.Persisted.Single().Select(m => m.MerchantId).ToArray());
        Assert.Single(rig.PersistedEvents.Single());
        rig.Health.Verify(h => h.RecordPollSuccessAsync(It.Is<IReadOnlyCollection<Guid>>(m => m.Single() == MerchantA), It.IsAny<CancellationToken>()), Times.Once());
        rig.Inbox.Verify(r => r.ReleasePollingAsync(rig.Lease, TimeSpan.Zero, null, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task QuarantinedMerchantIsLeftOutOfTheGroupAndTheGroupNeedsOnlyOneCall()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [MerchantB] = rig.Clock.GetUtcNow().AddMinutes(10) });
        rig.Poll(new HashSet<Guid> { MerchantB });

        await rig.Service.RunOnceAsync();

        Assert.Equal(new[] { "A" }, rig.Polled);
        rig.Health.Verify(h => h.RecordPollFailureAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task MerchantWhoseQuarantineExpiredIsRetriedAloneAndRecoversOnSuccess()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [MerchantB] = rig.Clock.GetUtcNow().AddSeconds(-1) });
        rig.Poll(new HashSet<Guid>());   // a autorizacao foi propagada: B ja responde

        await rig.Service.RunOnceAsync();

        Assert.Equal(new[] { "A", "B" }, rig.Polled);
        Assert.Equal(2, rig.Persisted.Single().Count);
        rig.Health.Verify(h => h.RecordPollSuccessAsync(It.Is<IReadOnlyCollection<Guid>>(m => m.Contains(MerchantB) && m.Contains(MerchantA)), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RetriedMerchantStillForbiddenStaysIsolatedWithoutAffectingOthers()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [MerchantB] = rig.Clock.GetUtcNow().AddSeconds(-1) });
        rig.Poll(new HashSet<Guid> { MerchantB });

        await rig.Service.RunOnceAsync();

        rig.Health.Verify(h => h.RecordPollFailureAsync(MerchantB, "IFOOD_EVENTS_FORBIDDEN", It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(new[] { MerchantA }, rig.Persisted.Single().Select(m => m.MerchantId).ToArray());
    }

    [Fact]
    public async Task EveryMerchantQuarantinedMeansNoPollingCallButAcksStillRun()
    {
        var rig = new Rig((MerchantB, StoreB));
        rig.Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [MerchantB] = rig.Clock.GetUtcNow().AddMinutes(5) });

        await rig.Service.RunOnceAsync();

        rig.Events.Verify(e => e.PollAsync(It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never());
        rig.Inbox.Verify(r => r.PersistAsync(It.IsAny<IfoodPollingLease>(), It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<IReadOnlyList<IfoodEventEnvelope>>(), It.IsAny<CancellationToken>()), Times.Never());
        rig.Inbox.Verify(r => r.PendingAcknowledgmentsAsync(rig.Lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("IFOOD_EVENTS_RATE_LIMIT", 429)]
    [InlineData("IFOOD_EVENTS_UNAUTHORIZED", 401)]
    [InlineData("IFOOD_EVENTS_REMOTE_ERROR", 500)]
    [InlineData("IFOOD_EVENTS_TRANSPORT", 0)]
    public async Task GlobalFailuresBackOffTheWholeCycleAndDoNotBlameAnyMerchant(string code, int status)
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Events.Setup(e => e.PollAsync(It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IfoodIntegrationException(code, status == 0 ? null : (HttpStatusCode)status, retryable: true, retryAfter: TimeSpan.FromSeconds(120)));

        var ex = await Assert.ThrowsAsync<IfoodIntegrationException>(() => rig.Service.RunOnceAsync());

        Assert.Equal(code, ex.Code);
        rig.Health.Verify(h => h.RecordPollFailureAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        rig.Inbox.Verify(r => r.ReleasePollingAsync(rig.Lease, TimeSpan.FromSeconds(120), code, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task EventsAlreadyReceivedAreStoredWhenALaterMerchantHitsAGlobalFailure()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Health.Setup(h => h.BlockedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, DateTimeOffset> { [MerchantA] = rig.Clock.GetUtcNow().AddSeconds(-1), [MerchantB] = rig.Clock.GetUtcNow().AddSeconds(-1) });
        var order = IfoodEventIngressTests.Envelope(MerchantA);
        rig.Events.Setup(e => e.PollAsync(It.Is<IReadOnlyList<IfoodPollingMerchant>>(m => m[0].MerchantId == MerchantA), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { order });
        rig.Events.Setup(e => e.PollAsync(It.Is<IReadOnlyList<IfoodPollingMerchant>>(m => m[0].MerchantId == MerchantB), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IfoodIntegrationException("IFOOD_EVENTS_RATE_LIMIT", HttpStatusCode.TooManyRequests, true));

        await Assert.ThrowsAsync<IfoodIntegrationException>(() => rig.Service.RunOnceAsync());

        Assert.Single(rig.PersistedEvents.Single());   // o pedido de A nao se perde
    }

    [Fact]
    public async Task AckRefusedForOneMerchantDoesNotStopTheAcksOfTheOthers()
    {
        var rig = new Rig((MerchantA, StoreA), (MerchantB, StoreB));
        rig.Poll(new HashSet<Guid>());
        var receiptA = new IfoodEventReceipt(MerchantA, StoreA, Guid.NewGuid());
        var receiptB = new IfoodEventReceipt(MerchantB, StoreB, Guid.NewGuid());
        rig.Inbox.SetupSequence(r => r.PendingAcknowledgmentsAsync(rig.Lease, It.IsAny<IReadOnlyList<IfoodPollingMerchant>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { receiptB, receiptA }).ReturnsAsync(Array.Empty<IfoodEventReceipt>());
        rig.Events.Setup(e => e.AcknowledgeAsync(It.Is<IReadOnlyList<IfoodEventReceipt>>(r => r[0].MerchantId == MerchantB), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IfoodIntegrationException("IFOOD_EVENTS_FORBIDDEN", HttpStatusCode.Forbidden));
        rig.Events.Setup(e => e.AcknowledgeAsync(It.Is<IReadOnlyList<IfoodEventReceipt>>(r => r[0].MerchantId == MerchantA), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await rig.Service.RunOnceAsync();

        rig.Inbox.Verify(r => r.AcknowledgedAsync(rig.Lease, It.Is<IReadOnlyList<IfoodEventReceipt>>(x => x.Single().MerchantId == MerchantA), It.IsAny<CancellationToken>()), Times.Once());
        rig.Inbox.Verify(r => r.AcknowledgmentFailedAsync(rig.Lease, It.Is<IReadOnlyList<IfoodEventReceipt>>(x => x.Single().MerchantId == MerchantB), "IFOOD_EVENTS_FORBIDDEN", TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("IFOOD_EVENTS_FORBIDDEN", 403, true)]
    [InlineData("IFOOD_MERCHANT_NOT_AUTHORIZED", 0, true)]
    [InlineData("IFOOD_EVENTS_REMOTE_ERROR", 404, true)]
    [InlineData("IFOOD_EVENTS_REMOTE_ERROR", 400, true)]
    [InlineData("IFOOD_EVENTS_REMOTE_ERROR", 500, false)]
    [InlineData("IFOOD_EVENTS_UNAUTHORIZED", 401, false)]
    [InlineData("IFOOD_EVENTS_RATE_LIMIT", 429, false)]
    [InlineData("IFOOD_EVENTS_TIMEOUT", 0, false)]
    public void OnlyMerchantScopedErrorsAreIsolated(string code, int status, bool scoped)
        => Assert.Equal(scoped, IfoodPollingService.IsMerchantScoped(new IfoodIntegrationException(code, status == 0 ? null : (HttpStatusCode)status)));
}
