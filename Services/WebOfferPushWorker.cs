using System.Net;
using System.Text.Json;
using APIBack.Service;
using Dapper;
using Npgsql;
using Lib.Net.Http.WebPush;

namespace APIBack.Services;

public sealed class WebOfferPushWorker(NpgsqlDataSource source, IHttpClientFactory factory, IConfiguration config, ILogger<WebOfferPushWorker> logger) : BackgroundService
{
    internal sealed class Pending
    {
        public long Id { get; set; }
        public string Token { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string P256dh { get; set; } = "";
        public string Auth { get; set; } = "";
        public Guid OfferId { get; set; }
        public Guid SessionId { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public bool Vibration { get; set; }
        public bool Sound { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (new WebOfferPushService(source, config).Configured) await PublishAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn) { }
            catch (Exception ex) { logger.LogWarning("Avisos web aguardam nova tentativa: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
    }

    internal static Task<IEnumerable<Pending>> ClaimAsync(NpgsqlConnection db, Guid lease, CancellationToken ct) => db.QueryAsync<Pending>(new CommandDefinition($"""
WITH picked AS (
 SELECT p.id FROM delivery_offer_push_outbox p
 JOIN delivery_web_push_subscription w ON w.token=p.token
 WHERE ((p.state='queued' AND p.available_at_utc<=NOW()) OR (p.state='sending' AND p.lease_until_utc<NOW()))
 AND p.attempts<5 AND {DeliveryOfferPushWorker.Valid}
 ORDER BY p.id LIMIT 20 FOR UPDATE OF p SKIP LOCKED
), claimed AS (
 UPDATE delivery_offer_push_outbox p SET state='sending',attempts=attempts+1,lease_id=@Lease,lease_until_utc=NOW()+INTERVAL '120 seconds'
 FROM picked WHERE p.id=picked.id RETURNING p.*
)
SELECT p.id AS Id,p.token AS Token,p.offer_id AS OfferId,p.session_id AS SessionId,p.expires_at_utc AS ExpiresAtUtc,
 w.endpoint AS Endpoint,w.p256dh AS P256dh,w.auth AS Auth,s.sound AS Sound,s.vibration AS Vibration
 FROM claimed p JOIN delivery_web_push_subscription w ON w.token=p.token JOIN delivery_chat_push_subscription s ON s.token=p.token
""", new { Lease = lease }, cancellationToken: ct));

    private async Task PublishAsync(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        var lease = Guid.NewGuid();
        var rows = await ClaimAsync(db, lease, ct);
        using var http = factory.CreateClient("web-offer-push");
        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct }, async (row, cancellationToken) =>
        {
            await using var rowDb = await source.OpenConnectionAsync(cancellationToken);
            var client = new PushServiceClient(http) { AutoRetryAfter = false };
            using var credentials = new WebOfferPushService(source, config).Credentials;
            var state = "sent"; string? errorCode = null; var remove = false;
            try
            {
                var current = await rowDb.ExecuteScalarAsync<bool>(new CommandDefinition($"SELECT EXISTS(SELECT 1 FROM delivery_offer_push_outbox p WHERE p.id=@Id AND p.lease_id=@Lease AND {DeliveryOfferPushWorker.Valid})", new { row.Id, Lease = lease }, cancellationToken: ct));
                if (!current) { state = "dismissed"; }
                else
                {
                    if (!WebOfferPushService.ValidSubscription(new(row.Endpoint, new(row.P256dh, row.Auth)))) throw new InvalidOperationException("PUSH_SUBSCRIPTION_INVALID");
                    var payload = JsonSerializer.Serialize(new { title = "Tem pedido novo pra você", body = "Confira a rota e escolha quais pedidos aceitar.", offerId = row.OfferId, recipientSessionId = row.SessionId, expiresAtUtc = row.ExpiresAtUtc, url = "/oferta", sound = row.Sound, vibration = row.Vibration });
                    await client.RequestPushMessageDeliveryAsync(new PushSubscription { Endpoint = row.Endpoint, Keys = new Dictionary<string, string> { ["p256dh"] = row.P256dh, ["auth"] = row.Auth } },
                        new PushMessage(payload) { TimeToLive = Math.Clamp((int)(row.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds, 1, 3600), Urgency = PushMessageUrgency.High }, credentials, ct);
                }
            }
            catch (PushServiceClientException ex)
            {
                remove = ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound;
                state = remove || ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "failed" : "queued";
                errorCode = "WEB_PUSH_HTTP_" + (int)ex.StatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { state = "queued"; errorCode = "PROVIDER_UNCONFIRMED"; logger.LogWarning("Oferta web não confirmada pelo provedor: {Type}", ex.GetType().Name); }
            await rowDb.ExecuteAsync(new CommandDefinition("""
UPDATE delivery_offer_push_outbox SET state=CASE WHEN @State='queued' AND attempts>=5 THEN 'failed' ELSE @State END,
 error_code=@ErrorCode,available_at_utc=NOW()+INTERVAL '10 seconds',lease_id=NULL,lease_until_utc=NULL WHERE id=@Id AND lease_id=@Lease;
""", new { row.Id, Lease = lease, State = state, ErrorCode = errorCode }, cancellationToken: ct));
            if (remove) await rowDb.ExecuteAsync(new CommandDefinition("DELETE FROM delivery_chat_push_subscription WHERE token=@Token AND session_id=@SessionId", new { row.Token, row.SessionId }, cancellationToken: ct));
        });
    }
}
