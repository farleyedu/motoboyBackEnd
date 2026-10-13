using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Events;
using APIBack.Tests.Unit;
using Npgsql;
using Xunit;
using Moq;
using System.Net;

namespace APIBack.Tests.Integration;

public sealed partial class IfoodIntegrationSchemaDatabaseTests
{
    private const string IngressFile = "20261013_04_ifood_event_ingress.sql";
    private static readonly string AppKey = IfoodInboxRepository.ApplicationKey("if06-test-app");
    private static async Task<Database> IngressDatabase()
    {
        var db = await Database.Create();
        try
        {
            await db.Migrate(IngressFile);
            await db.Execute("UPDATE ifood_connections SET status='authorized',authorized_at_utc=NOW()");
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static IfoodPollingMerchant[] Merchants(Database db) => new[]
    { new IfoodPollingMerchant { MerchantId=db.MerchantA,EstabelecimentoId=db.StoreA }, new IfoodPollingMerchant { MerchantId=db.MerchantB,EstabelecimentoId=db.StoreB } };
    private static async Task<IfoodPollingLease> Lease(Database db) => (await db.Inbox.AcquirePollingAsync(AppKey, 90, default))!;

    [DeliveryDatabaseFact]
    public async Task IngressMigrationIsRepeatableAndRollbackPreservesActiveData()
    {
        await using var db = await IngressDatabase();
        await db.Migrate(IngressFile);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_tracking_schema_versions"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Rollback()); // IF-04 recusa dependência IF-06.
        var down = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "rollback", "20261013_04_ifood_event_ingress.down.sql"));
        await db.Execute(down);
        await db.Migrate(IngressFile);
        await Lease(db);
        await Assert.ThrowsAsync<PostgresException>(() => db.Execute(down));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_polling_state"));
    }

    [DeliveryDatabaseFact]
    public async Task PollingLeaseIsSharedAcrossInstancesAndPersistsRateLimitAfterRestart()
    {
        await using var db = await IngressDatabase();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.Inbox.AcquirePollingAsync(AppKey, 90, default)));
        var lease = Assert.Single(results.Where(x => x != null))!;
        await db.Inbox.StartPollingRequestAsync(lease, 90, default);
        await db.Inbox.ReleasePollingAsync(lease, TimeSpan.Zero, null, default);
        Assert.Null(await db.Inbox.AcquirePollingAsync(AppKey, 90, default));
        await db.Execute("UPDATE ifood_polling_state SET next_poll_at_utc=NOW()-INTERVAL '1 second'");
        var restarted = await Lease(db);
        Assert.NotEqual(lease.LeaseId, restarted.LeaseId);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Inbox.RenewPollingAsync(lease, 90, default));
        await db.Inbox.ReleasePollingAsync(restarted, TimeSpan.FromSeconds(120), "IFOOD_EVENTS_RATE_LIMIT", default);
        Assert.True(await db.Scalar<bool>("SELECT next_poll_at_utc>NOW()+INTERVAL '119 seconds' FROM ifood_polling_state"));
    }

    [DeliveryDatabaseFact]
    public async Task PersistenceDeduplicatesBeforeAckAndRejectsForeignOrMalformedIdentities()
    {
        await using var db = await IngressDatabase();
        var lease = await Lease(db); var id = Guid.NewGuid();
        var item = IfoodEventIngressTests.Envelope(db.MerchantA, id);
        var malformed = System.Text.Json.JsonDocument.Parse("{\"id\":\"invalid\",\"secret\":\"retained\"}");
        using (malformed)
            await db.Inbox.PersistAsync(lease, Merchants(db), new[] { item, item,
                IfoodEventIngressTests.Envelope(Guid.NewGuid()), IfoodEventEnvelope.Parse(malformed.RootElement) }, default);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_event_rejections"));
        var receipts = await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default);
        Assert.Equal(id, Assert.Single(receipts).EventId);
        await db.Inbox.AcknowledgedAsync(lease, receipts, default);
        Assert.Empty(await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default));
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { item }, default);
        Assert.Single(await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
    }

    [DeliveryDatabaseFact]
    public async Task CollisionIsRetainedForReviewWithoutOverwritingOriginalAndMissingCodeIsNotDropped()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        var item = IfoodEventIngressTests.Envelope(db.MerchantA, marker: "original");
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { item }, default);
        using var altered = System.Text.Json.JsonDocument.Parse(item.PayloadJson.Replace("original", "changed"));
        using var unknown = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { id = Guid.NewGuid(), merchantId = db.MerchantA }));
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventEnvelope.Parse(altered.RootElement), IfoodEventEnvelope.Parse(unknown.RootElement) }, default);
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE state='review'"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_event_rejections WHERE payload->'metadata'->>'marker'='changed'"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE payload->'metadata'->>'marker'='original'"));
        Assert.Equal(2, (await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default)).Count);
    }

    [DeliveryDatabaseFact]
    public async Task RevocationAndWrongStoreRollbackEntirePersistenceAndNoAckIsEligible()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        var wrong = Merchants(db); wrong[0].EstabelecimentoId = db.StoreB;
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Inbox.PersistAsync(lease, wrong,
            new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
        await db.Execute("UPDATE ifood_connections SET status='revoked',revoked_at_utc=NOW()");
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Inbox.PersistAsync(lease, Merchants(db),
            new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default));
        Assert.Empty(await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default));
    }

    [DeliveryDatabaseFact]
    public async Task PendingAckSurvivesFailureAndNewRepositoryAndRespectsItsRetryDeadline()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default);
        var pending = await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default);
        await db.Inbox.AcknowledgmentFailedAsync(lease, pending, "IFOOD_EVENTS_TIMEOUT", TimeSpan.FromSeconds(120), default);
        Assert.Empty(await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default));
        Assert.Equal(1, await db.Scalar<int>("SELECT ack_attempts FROM ifood_events_inbox"));
        Assert.True(await db.Scalar<bool>("SELECT ack_next_attempt_at_utc>NOW()+INTERVAL '119 seconds' FROM ifood_events_inbox"));
        await db.Execute("UPDATE ifood_events_inbox SET ack_next_attempt_at_utc=NOW()-INTERVAL '1 second'");
        pending = await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default);
        Assert.Single(pending);
        await db.Inbox.AcknowledgedAsync(lease, pending, default);
        Assert.Equal("queued", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
    }

    [DeliveryDatabaseFact]
    public async Task ProcessingReservationsPreventDoubleClaimAndRejectStaleOwnersAfterRecovery()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        await db.Inbox.PersistAsync(lease, Merchants(db), Enumerable.Range(0, 4).Select(_ => IfoodEventIngressTests.Envelope(db.MerchantA)).ToArray(), default);
        var batches = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.Inbox.ClaimAsync(1, 90, 3)));
        var claimed = batches.SelectMany(b => b).ToArray();
        Assert.Equal(4, claimed.Length); Assert.Equal(4, claimed.Select(e => e.Id).Distinct().Count());
        Assert.Empty(await db.Inbox.ClaimAsync(1, 90, 3));
        Assert.True(await db.Inbox.RenewEventAsync(claimed[0], 90));
        await db.Execute("UPDATE ifood_events_inbox SET lease_until_utc=NOW()-INTERVAL '1 second' WHERE id=@Id", new { claimed[0].Id });
        var recovered = Assert.Single(await db.Inbox.ClaimAsync(1, 90, 3));
        Assert.Equal(claimed[0].Id, recovered.Id); Assert.Equal(2, recovered.Attempts);
        Assert.False(await db.Inbox.FinishEventAsync(claimed[0], IfoodInboxOutcome.Processed));
        Assert.False(await db.Inbox.RenewEventAsync(claimed[0], 90));
        Assert.True(await db.Inbox.FinishEventAsync(recovered, IfoodInboxOutcome.Review));
    }

    [DeliveryDatabaseFact]
    public async Task ProcessingRetryIsBoundedAndExpiredFinalAttemptIsRetainedAsFailed()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default);
        var first = Assert.Single(await db.Inbox.ClaimAsync(1, 90, 2));
        Assert.True(await db.Inbox.RetryEventAsync(first, 2, TimeSpan.FromSeconds(30)));
        Assert.Empty(await db.Inbox.ClaimAsync(1, 90, 2));
        await db.Execute("UPDATE ifood_events_inbox SET next_attempt_at_utc=NOW()-INTERVAL '1 second'");
        var second = Assert.Single(await db.Inbox.ClaimAsync(1, 90, 2));
        await db.Execute("UPDATE ifood_events_inbox SET lease_until_utc=NOW()-INTERVAL '1 second'");
        Assert.Empty(await db.Inbox.ClaimAsync(1, 90, 2));
        Assert.Equal("failed", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        Assert.False(await db.Inbox.FinishEventAsync(second, IfoodInboxOutcome.Processed));
    }

    [DeliveryDatabaseFact]
    public async Task ExpiredPollingOwnerCannotPersistOrMarkAckAndNewOwnerCanResume()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default);
        var pending = await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default);
        await db.Execute("UPDATE ifood_polling_state SET lease_until_utc=NOW()-INTERVAL '1 second',next_poll_at_utc=NOW()-INTERVAL '1 second'");
        var resumed = await Lease(db);
        Assert.NotEqual(lease.LeaseId, resumed.LeaseId);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Inbox.AcknowledgedAsync(lease, pending, default));
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => db.Inbox.PersistAsync(lease, Merchants(db), Array.Empty<IfoodEventEnvelope>(), default));
        await db.Inbox.AcknowledgedAsync(resumed, pending, default);
        Assert.Empty(await db.Inbox.PendingAcknowledgmentsAsync(resumed, Merchants(db), 2000, default));
    }

    [DeliveryDatabaseFact]
    public async Task CompleteHttpToDatabaseFlowPersistsBeforeAckAndRecoversLostResponseWithEmptyPolling()
    {
        await using var db = await IngressDatabase();
        var options = IfoodAuthenticationTests.EnabledOptions();
        options.PollingEnabled = true; options.PollingMerchants = Merchants(db).ToList();
        var tokens = new Mock<IIfoodTokenProvider>();
        tokens.Setup(t => t.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodAccessToken("test-only-token", DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromHours(1)));
        var access = new IfoodMerchantAccess(db.Connections, tokens.Object, Microsoft.Extensions.Options.Options.Create(options));
        var handler = new IfoodAuthenticationTests.Handler(); var polls = 0; var acknowledgments = 0;
        var incoming = IfoodEventIngressTests.Envelope(db.MerchantA);
        handler.Send = async (request, cancellation) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                polls++;
                return polls == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("["+incoming.PayloadJson+"]") }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            acknowledgments++;
            Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox"));
            Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE acknowledged_at_utc IS NULL"));
            // HTTP foi enviado depois do commit, mas sua primeira resposta desaparece.
            if (acknowledgments == 1) throw new TaskCanceledException("simulated lost ACK response");
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };
        var client = new IfoodEventsClient(new IngressHttpFactory(handler), access, tokens.Object, TimeProvider.System);
        IfoodPollingService NewProcess() => new(db.Inbox, client, Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        await Assert.ThrowsAsync<IfoodIntegrationException>(() => NewProcess().RunOnceAsync());
        Assert.Equal("queued", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        Assert.Equal(1, await db.Scalar<int>("SELECT ack_attempts FROM ifood_events_inbox"));
        await NewProcess().RunOnceAsync();
        Assert.Equal(1, polls); // Reiniciar não contorna prazo persistido de polling.
        await db.Execute("UPDATE ifood_polling_state SET next_poll_at_utc=NOW()-INTERVAL '1 second'; UPDATE ifood_events_inbox SET ack_next_attempt_at_utc=NOW()-INTERVAL '1 second'");
        await NewProcess().RunOnceAsync();
        Assert.Equal(2, polls); Assert.Equal(2, acknowledgments);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM ifood_events_inbox WHERE acknowledged_at_utc IS NOT NULL"));
        Assert.Equal("queued", await db.Scalar<string>("SELECT state FROM ifood_events_inbox"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM pedido")); // Receber evento não importa pedido ainda.
    }

    [DeliveryDatabaseFact]
    public async Task AcknowledgmentSelectionIsScopedToItsApplication()
    {
        await using var db = await IngressDatabase(); var lease = await Lease(db);
        await db.Inbox.PersistAsync(lease, Merchants(db), new[] { IfoodEventIngressTests.Envelope(db.MerchantA) }, default);
        var other = (await db.Inbox.AcquirePollingAsync(IfoodInboxRepository.ApplicationKey("another-test-app"), 90, default))!;
        Assert.Empty(await db.Inbox.PendingAcknowledgmentsAsync(other, Merchants(db), 2000, default));
        Assert.Single(await db.Inbox.PendingAcknowledgmentsAsync(lease, Merchants(db), 2000, default));
    }

    private sealed class IngressHttpFactory(IfoodAuthenticationTests.Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }
}
