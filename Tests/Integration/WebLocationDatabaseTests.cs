using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task StaleWebGpsRemainsOnMapAndDoesNotChangeDeliveryOrHeartbeatAge()
    {
        await using var db = await CompletionDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET device_type='web', expires_at_utc=NOW()+INTERVAL '12 hours', last_heartbeat_at_utc=NOW()-INTERVAL '7 minutes'");
        var point = Database.Point(1);
        point.CapturedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-7).ToUnixTimeMilliseconds());
        await db.Repository.WriteLocationAsync(db.SessionId, 1, 4, point);
        var snapshot = await db.Repository.GetSnapshotAsync(db.StoreId);
        var courier = Assert.Single(snapshot.Motoboys);
        Assert.False(courier.HasRecentLocation);
        Assert.Equal("delivering", courier.Status);
        Assert.NotNull(courier.Location);
        Assert.Equal(point.CapturedAtUtc, courier.Location!.CapturedAtUtc);
        var received = courier.Location.ReceivedAtUtc;
        await db.Repository.ExpireDueSessionsAsync(100);
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        var resumed = Assert.Single((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal(point.CapturedAtUtc, resumed.Location!.CapturedAtUtc);
        Assert.Equal(received, resumed.Location.ReceivedAtUtc);
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
        var error = await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.EndSessionAsync(db.SessionId, 1, 4, "logout"));
        Assert.Equal("MOTOBOY_HAS_PENDING_WORK", error.Code);
    }
}
