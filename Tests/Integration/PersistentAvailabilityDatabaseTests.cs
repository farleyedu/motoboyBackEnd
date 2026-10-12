using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task NativeAndWebKeepTheSameTurnAfterLongBackgroundAndTokenRenewal()
    {
        foreach (var platform in new[] { "native", "web" })
        {
            await using var db = await ResumeDatabase();
            await db.Execute("UPDATE motoboy_active_sessions SET client_instance_id='aparelho', device_type=@Platform, expires_at_utc=NOW()-INTERVAL '30 days', last_heartbeat_at_utc=NOW()-INTERVAL '30 days'", new { Platform = platform == "web" ? "web" : "mobile" });
            Assert.Equal(0, await db.Repository.ExpireDueSessionsAsync(100));
            Assert.Null((await db.Repository.GetSessionAsync(db.SessionId))!.EndedAtUtc);
            var renewed = await db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "aparelho", clientPlatform: platform, resumeSessionId: db.SessionId);
            Assert.Equal(db.SessionId, renewed.SessionId);
            Assert.Equal(4, renewed.SessionEpoch);
            Assert.Equal(720, (renewed.ExpiresAtUtc - renewed.LastHeartbeatAtUtc).TotalSeconds);
            Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
            Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_current"));
            var conflict = await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "outro-aparelho", resumeSessionId: db.SessionId));
            Assert.Equal("SESSION_CONFLICT", conflict.Code);
            await db.Execute("UPDATE delivery_route_stops SET stop_status='cancelled'");
            await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "motoboy_offline");
            Assert.Equal("SESSION_CHANGED", (await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "aparelho", resumeSessionId: db.SessionId))).Code);
        }
    }

    [DeliveryDatabaseFact]
    public async Task MigrationPreservesExplicitEndsAndRevocationsAndDoesNotRefreshGps()
    {
        await using var db = await ResumeDatabase();
        await db.Execute("""
CREATE TABLE motoboy_login_sessions(session_id uuid,id_usuario integer,expires_at_utc timestamptz,revoked_at_utc timestamptz);
CREATE TABLE usuario_refresh_tokens(motoboy_login_session_id uuid,expires_at timestamptz,revoked_at timestamptz);
INSERT INTO motoboy_login_sessions VALUES(@SessionId,7,NOW()-INTERVAL '1 day',NULL);
INSERT INTO usuario_refresh_tokens VALUES(@SessionId,NOW()-INTERVAL '1 day',NULL);
UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 day';
""", new { db.SessionId });
        await ApplyCorrection(db, "20261011_01_disponibilidade_persistente.sql");
        await ApplyCorrection(db, "20261011_01_disponibilidade_persistente.sql");
        Assert.Equal(OperationalAvailability.UntilExplicitEnd, await db.Scalar<DateTimeOffset>("SELECT expires_at_utc FROM motoboy_active_sessions"));
        Assert.Equal(OperationalAvailability.UntilExplicitEnd, await db.Scalar<DateTimeOffset>("SELECT expires_at FROM usuario_refresh_tokens"));
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW(),end_reason='motoboy_offline',expires_at_utc=NOW()-INTERVAL '1 day'; UPDATE motoboy_login_sessions SET revoked_at_utc=NOW(),expires_at_utc=NOW()-INTERVAL '1 day'; UPDATE usuario_refresh_tokens SET revoked_at=NOW(),expires_at=NOW()-INTERVAL '1 day'");
        await ApplyCorrection(db, "20261011_01_disponibilidade_persistente.sql");
        Assert.True(await db.Scalar<bool>("SELECT expires_at_utc<NOW() FROM motoboy_active_sessions"));
        Assert.True(await db.Scalar<bool>("SELECT expires_at_utc<NOW() FROM motoboy_login_sessions"));
        Assert.True(await db.Scalar<bool>("SELECT expires_at<NOW() FROM usuario_refresh_tokens"));
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_current"));
    }
}
