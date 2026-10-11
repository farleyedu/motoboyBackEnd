using System.Security.Cryptography;
using System.Text;
using APIBack.DTOs.Atendimento;
using Dapper;
using Npgsql;
using Lib.Net.Http.WebPush.Authentication;

namespace APIBack.Service;

public sealed record WebPushKeys(string P256dh, string Auth);
public sealed record WebPushRegistration(string Endpoint, WebPushKeys Keys, bool Sound = true, bool Vibration = true);

public sealed class WebOfferPushService(NpgsqlDataSource source, IConfiguration config)
{
    public bool Configured
    {
        get
        {
            if (string.IsNullOrWhiteSpace(config["Delivery:WebPush:PublicKey"]) || string.IsNullOrWhiteSpace(config["Delivery:WebPush:PrivateKey"])
                || !Uri.TryCreate(config["Delivery:WebPush:Subject"], UriKind.Absolute, out var subject) || subject.Scheme is not ("mailto" or "https")) return false;
            try { using var credentials = Credentials; return true; }
            catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException) { return false; }
        }
    }
    public string? PublicKey => Configured ? config["Delivery:WebPush:PublicKey"] : null;
    public VapidAuthentication Credentials => new(config["Delivery:WebPush:PublicKey"], config["Delivery:WebPush:PrivateKey"]) { Subject = config["Delivery:WebPush:Subject"] };

    public static bool ValidSubscription(WebPushRegistration request)
    {
        if (request.Keys == null || request.Keys.P256dh?.Length > 128 || request.Keys.Auth?.Length > 32 || request.Endpoint?.Length > 2048 || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        // Endpoints são capacidades privadas; permitir somente provedores de push.
        var host = uri.IdnHost.ToLowerInvariant();
        if (host != "fcm.googleapis.com" && host != "updates.push.services.mozilla.com" && host != "web.push.apple.com"
            && !host.EndsWith(".notify.windows.com", StringComparison.Ordinal) && !host.EndsWith(".notifications.microsoft.com", StringComparison.Ordinal)) return false;
        try
        {
            static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            var publicKey = Decode(request.Keys.P256dh);
            return publicKey.Length == 65 && publicKey[0] == 4 && Decode(request.Keys.Auth).Length == 16;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NullReferenceException) { return false; }
    }

    public async Task RegisterAsync(ChatActor actor, Guid sessionId, long epoch, WebPushRegistration request)
    {
        if (!Configured) throw new DeliveryDomainException(503, "WEB_PUSH_UNCONFIGURED", "Os avisos com o app fechado ainda precisam ser habilitados pela equipe.");
        if (!actor.MotoboyId.HasValue || !ValidSubscription(request)) throw new DeliveryDomainException(422, "PUSH_SUBSCRIPTION_INVALID", "A inscrição de notificações não é válida.");
        var token = "web:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Endpoint)));
        await using var db = await source.OpenConnectionAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@Token,0))", new { Token = token }, tx);
        if (!await db.ExecuteScalarAsync<bool>("""
SELECT EXISTS(SELECT 1 FROM motoboy_active_sessions s
 JOIN motoboy_estabelecimento me ON me.motoboy_id=s.motoboy_id AND me.estabelecimento_id=s.id_estabelecimento AND me.ativo=TRUE
 WHERE s.session_id=@SessionId AND s.session_epoch=@Epoch AND s.motoboy_id=@MotoboyId AND s.id_usuario=@UserId
 AND s.id_estabelecimento=@EstablishmentId AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL)
""", new { SessionId = sessionId, Epoch = epoch, actor.MotoboyId, UserId = actor.UserId, actor.EstablishmentId }, tx))
            throw new DeliveryDomainException(401, "SESSION_EXPIRED", "Seu turno mudou. Atualize o app.");
        await db.ExecuteAsync("""
UPDATE delivery_offer_push_outbox SET state='dismissed' WHERE token=@Token AND session_id<>@SessionId AND state IN ('queued','sending');
INSERT INTO delivery_chat_push_subscription(token,estabelecimento_id,motoboy_id,session_id,sound,vibration,muted,mention_alerts)
 VALUES(@Token,@EstablishmentId,@MotoboyId,@SessionId,@Sound,@Vibration,FALSE,TRUE)
 ON CONFLICT(token) DO UPDATE SET estabelecimento_id=EXCLUDED.estabelecimento_id,motoboy_id=EXCLUDED.motoboy_id,
 session_id=EXCLUDED.session_id,sound=EXCLUDED.sound,vibration=EXCLUDED.vibration,updated_at_utc=NOW();
INSERT INTO delivery_web_push_subscription(token,endpoint,p256dh,auth) VALUES(@Token,@Endpoint,@P256dh,@Auth)
 ON CONFLICT(token) DO UPDATE SET p256dh=EXCLUDED.p256dh,auth=EXCLUDED.auth;
""", new { Token = token, actor.EstablishmentId, actor.MotoboyId, SessionId = sessionId, request.Sound, request.Vibration, request.Endpoint, request.Keys.P256dh, request.Keys.Auth }, tx);
        await tx.CommitAsync();
    }
}
