using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task PresenceExpiresWithoutEndingTurnAndGpsAndHeartbeatRenewIt()
    {
        await using var db = await ResumeDatabase();
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        Assert.Single((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 second'");
        Assert.Empty((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal(0, await db.Repository.ExpireDueSessionsAsync(100));
        Assert.Null((await db.Repository.GetSessionAsync(db.SessionId))!.EndedAtUtc);
        await db.Repository.WriteLocationAsync(db.SessionId, 1, 4, Database.Point(1));
        var online = Assert.Single((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.True(online.PresenceExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(11));
        var session = await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        Assert.Equal(720, (session.ExpiresAtUtc - session.LastHeartbeatAtUtc).TotalSeconds);
        await db.Execute("UPDATE delivery_route_stops SET stop_status='cancelled'");
        await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "client_end");
        Assert.Empty((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.HeartbeatAsync(db.SessionId, 1, 4))).Code);
    }

    [DeliveryDatabaseFact]
    public async Task PresenceMigrationDoesNotReopenEndedSessionsOrRenewOldSignal()
    {
        await using var db = await ResumeDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc='9999-12-31',last_seen_at=NOW()-INTERVAL '13 minutes'");
        await ApplyCorrection(db, "20261012_01_presence_window.sql");
        Assert.Empty((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Null((await db.Repository.GetSessionAsync(db.SessionId))!.EndedAtUtc);
        await ApplyCorrection(db, "20261012_01_presence_window.sql");
        Assert.True(await db.Scalar<bool>("SELECT expires_at_utc<NOW() FROM motoboy_active_sessions"));
    }
}
