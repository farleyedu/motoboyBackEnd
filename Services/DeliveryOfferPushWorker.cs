using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace APIBack.Services;

public sealed class DeliveryOfferPushWorker(NpgsqlDataSource source, IHttpClientFactory factory, IConfiguration config, ILogger<DeliveryOfferPushWorker> logger) : BackgroundService
{
    private sealed class Pending
    {
        public long Id { get; set; } public Guid OfferId { get; set; } public string Token { get; set; } = "";
        public Guid SessionId { get; set; } public Guid EstabelecimentoId { get; set; } public int MotoboyId { get; set; }
        public bool Sound { get; set; } public bool Vibration { get; set; } public DateTimeOffset ExpiresAtUtc { get; set; }
        public string? TicketId { get; set; }
    }
    private const string Valid = @"p.expires_at_utc>NOW() AND EXISTS(SELECT 1 FROM delivery_chat_push_subscription sub
 JOIN motoboy_active_sessions a ON a.session_id=sub.session_id AND a.motoboy_id=sub.motoboy_id AND a.id_estabelecimento=sub.estabelecimento_id
 JOIN motoboy_estabelecimento me ON me.motoboy_id=sub.motoboy_id AND me.estabelecimento_id=sub.estabelecimento_id
 WHERE sub.token=p.token AND sub.session_id=p.session_id AND sub.motoboy_id=p.motoboy_id AND sub.estabelecimento_id=p.estabelecimento_id
 AND a.ended_at_utc IS NULL AND a.revoked_at IS NULL AND a.expires_at_utc>NOW() AND me.ativo=TRUE)
 AND EXISTS(SELECT 1 FROM delivery_route_stops s WHERE s.offer_id=p.offer_id AND s.estabelecimento_id=p.estabelecimento_id
 AND s.motoboy_id=p.motoboy_id AND s.offered_at_utc IS NOT NULL AND s.stop_status='assigned')";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var receiptAt = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue<bool?>("Delivery:OfferPushEnabled") ?? true)
                {
                    await PublishAsync(ct);
                    if (DateTimeOffset.UtcNow >= receiptAt) { await ReceiptsAsync(ct); receiptAt = DateTimeOffset.UtcNow.AddMinutes(1); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn) { /* Migration deve anteceder habilitação do recurso. */ }
            catch (Exception ex) { logger.LogWarning("Avisos de oferta aguardam nova tentativa: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
    }
    private HttpClient Client()
    {
        var client = factory.CreateClient(); client.Timeout = TimeSpan.FromSeconds(15);
        var key = config["Communication:ExpoAccessToken"];
        if (!string.IsNullOrWhiteSpace(key)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
    private async Task PublishAsync(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        var lease = Guid.NewGuid();
        await db.ExecuteAsync(new CommandDefinition("UPDATE delivery_offer_push_outbox SET state='failed',lease_id=NULL,lease_until_utc=NULL,error_code='ATTEMPTS_EXHAUSTED' WHERE state='sending' AND attempts>=5 AND lease_until_utc<NOW()", cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition($@"UPDATE delivery_offer_push_outbox p SET state='dismissed',lease_id=NULL,lease_until_utc=NULL
 WHERE state IN ('queued','sending') AND NOT ({Valid});", cancellationToken: ct));
        var rows = (await db.QueryAsync<Pending>(new CommandDefinition($@"WITH picked AS (
 SELECT p.id FROM delivery_offer_push_outbox p WHERE ((p.state='queued' AND p.available_at_utc<=NOW())
 OR (p.state='sending' AND p.lease_until_utc<NOW())) AND p.attempts<5 AND {Valid}
 ORDER BY p.id LIMIT 20 FOR UPDATE SKIP LOCKED
 ), claimed AS (UPDATE delivery_offer_push_outbox p SET state='sending',attempts=attempts+1,lease_id=@Lease,lease_until_utc=NOW()+interval '30 seconds'
 FROM picked WHERE p.id=picked.id RETURNING p.*)
 SELECT p.id AS Id,p.offer_id AS OfferId,p.token AS Token,p.session_id AS SessionId,p.expires_at_utc AS ExpiresAtUtc,
 s.sound AS Sound,s.vibration AS Vibration FROM claimed p JOIN delivery_chat_push_subscription s ON s.token=p.token;", new { Lease = lease }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return;
        try
        {
            using var client = Client();
            var payload = rows.Select(row => new {
                to = row.Token, title = "Tem pedido novo pra você", body = "Confira os pedidos e escolha quais aceitar.",
                priority = "high", sound = row.Sound ? "default" : null,
                channelId = "delivery-offers-" + (row.Sound ? "sound" : "silent") + "-" + (row.Vibration ? "vibrate" : "quiet") + "-v1",
                collapseId = row.OfferId.ToString(), tag = row.OfferId.ToString(),
                ttl = Math.Clamp((int)(row.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds, 1, 3600),
                data = new { offerId = row.OfferId, recipientSessionId = row.SessionId, expiresAtUtc = row.ExpiresAtUtc, url = "zippygomotoboy:///oferta" }
            }).ToArray();
            using var response = await client.PostAsJsonAsync("https://exp.host/--/api/v2/push/send", payload, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tickets = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
            if (tickets.Length != rows.Count) throw new InvalidOperationException("PUSH_TICKET_COUNT");
            for (var i = 0; i < rows.Count; i++)
            {
                var ticket = tickets[i]; var success = ticket.GetProperty("status").GetString() == "ok";
                var error = success ? null : Error(ticket);
                var retry = error == "MessageRateExceeded";
                await db.ExecuteAsync(new CommandDefinition(@"UPDATE delivery_offer_push_outbox SET state=@State,ticket_id=@Ticket,error_code=@Error,
 available_at_utc=NOW()+interval '30 seconds',lease_id=NULL,lease_until_utc=NULL WHERE id=@Id AND lease_id=@Lease;",
                    new { rows[i].Id, Lease = lease, State = success ? "sent" : retry ? "queued" : "failed", Ticket = success ? ticket.GetProperty("id").GetString() : null, Error = error }, cancellationToken: ct));
                if (error == "DeviceNotRegistered") await RemoveTokenAsync(db, rows[i].Token, rows[i].SessionId, ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await db.ExecuteAsync(new CommandDefinition(@"UPDATE delivery_offer_push_outbox SET state=CASE WHEN attempts>=5 THEN 'failed' ELSE 'queued' END,
 available_at_utc=NOW()+make_interval(secs=>LEAST(60,5*attempts)),lease_id=NULL,lease_until_utc=NULL,error_code='PROVIDER_UNCONFIRMED'
 WHERE id=ANY(@Ids) AND lease_id=@Lease;", new { Ids = rows.Select(r => r.Id).ToArray(), Lease = lease }, cancellationToken: ct));
        }
    }
    private static string? Error(JsonElement ticket) => ticket.TryGetProperty("details", out var d) && d.TryGetProperty("error", out var e) ? e.GetString() : "PROVIDER_ERROR";
    private static Task RemoveTokenAsync(NpgsqlConnection db, string token, Guid session, CancellationToken ct) => db.ExecuteAsync(new CommandDefinition("DELETE FROM delivery_chat_push_subscription WHERE token=@Token AND session_id=@Session;", new { Token = token, Session = session }, cancellationToken: ct));
    private async Task ReceiptsAsync(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        var rows = (await db.QueryAsync<Pending>(new CommandDefinition(@"SELECT id AS Id,token AS Token,session_id AS SessionId,ticket_id AS TicketId
 FROM delivery_offer_push_outbox WHERE state='sent' AND ticket_id IS NOT NULL AND created_at_utc<NOW()-interval '1 minute'
 AND created_at_utc>NOW()-interval '24 hours' AND (receipt_checked_at_utc IS NULL OR receipt_checked_at_utc<NOW()-interval '1 minute') ORDER BY id LIMIT 100;", cancellationToken: ct))).ToList();
        if (rows.Count == 0) return;
        using var client = Client(); using var response = await client.PostAsJsonAsync("https://exp.host/--/api/v2/push/getReceipts", new { ids = rows.Select(r => r.TicketId).ToArray() }, ct);
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var receipts = json.RootElement.GetProperty("data");
        foreach (var row in rows)
        {
            var state = "sent"; string? error = null;
            if (receipts.TryGetProperty(row.TicketId!, out var receipt)) { state = receipt.GetProperty("status").GetString() == "ok" ? "delivered" : "failed"; error = state == "failed" ? Error(receipt) : null; }
            await db.ExecuteAsync(new CommandDefinition("UPDATE delivery_offer_push_outbox SET state=@State,error_code=@Error,receipt_checked_at_utc=NOW() WHERE id=@Id AND state='sent';", new { row.Id, State = state, Error = error }, cancellationToken: ct));
            if (error == "DeviceNotRegistered") await RemoveTokenAsync(db, row.Token, row.SessionId, ct);
        }
    }
}
