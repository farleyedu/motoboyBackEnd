using System.Net;
using System.Reflection;
using APIBack.Service;
using APIBack.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using APIBack.Tests.Unit;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    private sealed class WebOfferHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("web.push.apple.com", request.RequestUri!.Host);
            Assert.Equal("high", Assert.Single(request.Headers.GetValues("Urgency")));
            Assert.InRange(int.Parse(Assert.Single(request.Headers.GetValues("TTL"))), 1, 300);
            Assert.True((await request.Content!.ReadAsByteArrayAsync(ct)).Length > 100);
            Assert.Equal("aes128gcm", Assert.Single(request.Content.Headers.ContentEncoding));
            Sends++;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    [DeliveryDatabaseFact]
    public async Task WebOfferPushIsEncryptedUrgentAndNotSentAgainOrAfterExplicitOffline()
    {
        await using var db = await WorkDatabase();
        await db.Execute("""
CREATE TABLE delivery_chat_push_subscription(token text PRIMARY KEY,estabelecimento_id uuid NOT NULL,motoboy_id int NOT NULL,
 session_id uuid NOT NULL,sound bool NOT NULL,vibration bool NOT NULL,muted bool,mention_alerts bool,updated_at_utc timestamptz DEFAULT NOW());
UPDATE motoboy_active_sessions SET expires_at_utc='9999-12-31 00:00:00+00',last_heartbeat_at_utc=NOW()-INTERVAL '30 days';
UPDATE delivery_route_stops SET stop_status='assigned',picked_up_at_utc=NULL,offered_at_utc=NOW(),offer_id=@Offer;
""", new { Offer = Guid.NewGuid() });
        await ApplyCorrection(db, "20261009_03_offer_push.sql");
        await ApplyCorrection(db, "20261011_02_oferta_web_push.sql");
        await ApplyCorrection(db, "20261011_02_oferta_web_push.sql");
        var keys = WebOfferPushTests.Keys();
        var deviceKeys = WebOfferPushTests.Keys();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Delivery:WebPush:Subject"]="https://qa.exemplo.invalid",["Delivery:WebPush:PublicKey"]=keys.PublicKey,["Delivery:WebPush:PrivateKey"]=keys.PrivateKey
        }).Build();
        var service = new WebOfferPushService(db.Source, config);
        var request = new WebPushRegistration("https://web.push.apple.com/test", new(deviceKeys.PublicKey, Convert.ToBase64String(new byte[16])));
        await service.RegisterAsync(new(db.StoreId, 7, 1), db.SessionId, 4, request);
        await service.RegisterAsync(new(db.StoreId, 7, 1), db.SessionId, 4, request);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_offer_push_outbox"));
        using var handler = new WebOfferHandler();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("web-offer-push")).Returns(() => new HttpClient(handler, false));
        using var worker = new WebOfferPushWorker(db.Source, factory.Object, config, NullLogger<WebOfferPushWorker>.Instance);
        async Task Publish() => await (Task)typeof(WebOfferPushWorker).GetMethod("PublishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, new object[] { CancellationToken.None })!;
        await Publish();
        Assert.Equal("sent", await db.Scalar<string>("SELECT state FROM delivery_offer_push_outbox"));
        await Publish(); Assert.Equal(1, handler.Sends);
        await db.Execute("UPDATE delivery_offer_push_outbox SET state='queued';UPDATE motoboy_active_sessions SET ended_at_utc=NOW(),end_reason='motoboy_offline'");
        await Publish(); Assert.Equal(1, handler.Sends);
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => service.RegisterAsync(new(db.StoreId, 7, 1), db.SessionId, 4, request))).Code);
    }
}
