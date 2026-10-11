using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    private static async Task<Database> ResumeDatabase()
    {
        var db = await CompletionDatabase();
        await db.Execute("""
CREATE SEQUENCE motoboy_session_epoch_seq START 5;
ALTER TABLE motoboy_active_sessions ADD COLUMN contract_version INTEGER NOT NULL DEFAULT 2,
    ADD COLUMN created_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
CREATE TABLE motoboy_operational_session_events (
    event_id UUID PRIMARY KEY, session_id UUID NOT NULL REFERENCES motoboy_active_sessions(session_id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id), estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    event_type TEXT NOT NULL, actor_user_id INTEGER, reason TEXT, details JSONB,
    occurred_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW());
""");
        return db;
    }

    [DeliveryDatabaseFact]
    public async Task AutomaticResumeOnlyRecoversHeartbeatTimeoutAndDoesNotReplaceAnotherDevice()
    {
        await using var db = await ResumeDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW(),end_reason='heartbeat_timeout',expires_at_utc=NOW()-INTERVAL '1 second';");
        var attempt = Guid.NewGuid();
        var recovered = await db.Repository.StartMobileSessionAsync(7, db.StoreId, attempt, "mesmo-aparelho", resumeSessionId: db.SessionId);
        Assert.NotEqual(db.SessionId, recovered.SessionId);
        Assert.Equal("heartbeat_timeout", (await db.Repository.GetSessionAsync(db.SessionId))!.EndReason);
        Assert.Equal(recovered.SessionId, (await db.Repository.StartMobileSessionAsync(7, db.StoreId, attempt, "mesmo-aparelho", resumeSessionId: db.SessionId)).SessionId);
        var conflict = await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "outro-aparelho", resumeSessionId: db.SessionId));
        Assert.Equal("SESSION_CONFLICT", conflict.Code);
        Assert.Null((await db.Repository.GetSessionAsync(recovered.SessionId))!.EndedAtUtc);
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
    }

    [DeliveryDatabaseFact]
    public async Task AutomaticResumeRejectsExplicitEndAndDifferentOwner()
    {
        await using var db = await ResumeDatabase();
        await db.Execute("UPDATE delivery_route_stops SET stop_status='cancelled';");
        await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "motoboy_offline");
        var ended = await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "qa", resumeSessionId: db.SessionId));
        Assert.Equal("SESSION_CHANGED", ended.Code);
        await db.Execute("UPDATE motoboy_active_sessions SET end_reason='heartbeat_timeout',started_by_user_id=8;");
        var other = await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "qa", resumeSessionId: db.SessionId));
        Assert.Equal("SESSION_CHANGED", other.Code);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_active_sessions"));
    }

    [DeliveryDatabaseFact]
    public async Task MandatoryAcceptanceCannotBeDisabledByLegacySettings()
    {
        await using var db = await CompletionDatabase();
        var repo = new PedidoQueueRepository(db.Source);
        var settings = await repo.GetSettingsAsync(db.StoreId);
        Assert.True(settings.RequireMotoboyAcceptance);
        await ApplyCorrection(db, "20261010_03_aceite_obrigatorio.sql");
        await ApplyCorrection(db, "20261010_03_aceite_obrigatorio.sql");
        Assert.True(await db.Scalar<bool>("SELECT require_motoboy_acceptance FROM delivery_settings"));
    }
}
