using APIBack.DTOs.Delivery;
using APIBack.Repository;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Integration;

public partial class DeliverySyncDatabaseTests
{
    private static async Task<Database> PresenceDatabase()
    {
        var db = await ResumeDatabase();
        await db.Execute("""
CREATE TABLE usuario(id int PRIMARY KEY, nome text);
INSERT INTO usuario VALUES(7,'Motoboy'),(9,'Atendente');
ALTER TABLE delivery_route_stops ADD COLUMN IF NOT EXISTS removed_at_utc timestamptz;
""");
        await ApplyCorrection(db, "20261104_01_presenca_7h_incidentes.sql");
        return db;
    }

    [DeliveryDatabaseFact]
    public async Task ShiftStaysOnlineWithoutHeartbeatAndOnlyHumanActivityMovesTheSevenHourDeadline()
    {
        await using var db = await PresenceDatabase();
        await db.Execute("UPDATE delivery_route_stops SET stop_status='cancelled'");
        Assert.InRange(await db.Scalar<double>("SELECT EXTRACT(EPOCH FROM expires_at_utc-NOW()) FROM motoboy_active_sessions"), 7 * 3600 - 60, 7 * 3600 + 1);

        // PWA fechado há 30 minutos: sem heartbeat nem GPS, mas o turno continua listado e elegível.
        await db.Execute("UPDATE motoboy_active_sessions SET last_heartbeat_at_utc=NOW()-INTERVAL '30 minutes', last_seen_at=NOW()-INTERVAL '30 minutes'");
        var stale = Assert.Single((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal("disconnected", stale.PresenceStatus);
        Assert.Equal("online", stale.Status);
        Assert.InRange(stale.InactivityRemainingSeconds, 7 * 3600 - 120, 7 * 3600);
        Assert.NotNull(stale.LastHumanActivityAtUtc);
        Assert.Equal(0, await db.Repository.ExpireDueSessionsAsync(100));

        // Heartbeat e GPS reconectam, mas não adiam as 7 horas.
        await db.Execute("UPDATE motoboy_active_sessions SET last_human_activity_at_utc=NOW()-INTERVAL '6 hours', expires_at_utc=NOW()+INTERVAL '1 hour'");
        var deadline = await db.Scalar<DateTimeOffset>("SELECT expires_at_utc FROM motoboy_active_sessions");
        await db.Repository.HeartbeatAsync(db.SessionId, 1, 4);
        await db.Repository.WriteLocationAsync(db.SessionId, 1, 4, Database.Point(1));
        Assert.Equal(deadline, await db.Scalar<DateTimeOffset>("SELECT expires_at_utc FROM motoboy_active_sessions"));
        var connected = Assert.Single((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal("connected", connected.PresenceStatus);

        // Atividade humana renova, com limite de frequência.
        var first = await db.Repository.RecordActivityAsync(db.SessionId, 1, 4);
        Assert.True(first.Recorded);
        Assert.InRange((first.ExpiresAtUtc - first.ServerTimeUtc).TotalHours, 6.99, 7.01);
        var second = await db.Repository.RecordActivityAsync(db.SessionId, 1, 4);
        Assert.False(second.Recorded);
        Assert.Equal(first.LastHumanActivityAtUtc, second.LastHumanActivityAtUtc);
        await Assert.ThrowsAsync<DeliveryDomainException>(() => db.Repository.RecordActivityAsync(db.SessionId, 1, 99));

        // Fim do prazo: encerra uma única vez, por inatividade, sem apagar histórico nem GPS.
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 second'");
        Assert.Empty((await db.Repository.GetSnapshotAsync(db.StoreId)).Motoboys);
        Assert.Equal(1, await db.Repository.ExpireDueSessionsAsync(100));
        Assert.Equal(0, await db.Repository.ExpireDueSessionsAsync(100));
        var ended = (await db.Repository.GetSessionAsync(db.SessionId))!;
        Assert.Equal("inactivity_7h", ended.EndReason);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_operational_session_events WHERE event_type='session.ended'"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM motoboy_location_samples"));
        Assert.Equal("SESSION_EXPIRED", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.HeartbeatAsync(db.SessionId, 1, 4))).Code);
    }

    [DeliveryDatabaseFact]
    public async Task PresenceMigrationDoesNotReopenEndedSessionsOrRenewOldSignal()
    {
        // Migração histórica de 12 minutos (substituída pela janela de 7 h, mas ainda precisa ser reaplicável sem reabrir turnos).
        await using var db = await ResumeDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc='9999-12-31',last_seen_at=NOW()-INTERVAL '13 minutes'");
        await ApplyCorrection(db, "20261012_01_presence_window.sql");
        Assert.Null((await db.Repository.GetSessionAsync(db.SessionId))!.EndedAtUtc);
        await ApplyCorrection(db, "20261012_01_presence_window.sql");
        Assert.True(await db.Scalar<bool>("SELECT expires_at_utc<NOW() FROM motoboy_active_sessions"));
    }

    [DeliveryDatabaseFact]
    public async Task PresenceMigrationGivesOpenShiftsSevenHoursOnceAndDoesNotReopenEnded()
    {
        await using var db = await ResumeDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 second'");
        await ApplyCorrection(db, "20261104_01_presenca_7h_incidentes.sql");
        var deadline = await db.Scalar<DateTimeOffset>("SELECT expires_at_utc FROM motoboy_active_sessions");
        Assert.InRange((deadline - DateTimeOffset.UtcNow).TotalHours, 6.99, 7.01);
        await ApplyCorrection(db, "20261104_01_presenca_7h_incidentes.sql");
        Assert.Equal(deadline, await db.Scalar<DateTimeOffset>("SELECT expires_at_utc FROM motoboy_active_sessions"));
        await db.Execute("UPDATE motoboy_active_sessions SET ended_at_utc=NOW(), revoked_at=NOW(), end_reason='client_end', expires_at_utc=NOW()-INTERVAL '1 hour', last_human_activity_at_utc=NULL");
        await ApplyCorrection(db, "20261104_01_presenca_7h_incidentes.sql");
        Assert.True(await db.Scalar<bool>("SELECT expires_at_utc<NOW() FROM motoboy_active_sessions"));
    }

    [DeliveryDatabaseFact]
    public async Task EndingWithOrdersRequiresConfirmationAndOpensOneIncidentKeepingCollectedOrders()
    {
        await using var db = await PresenceDatabase();
        // Pedido 24 ainda não foi retirado; o 23 está com o motoboy.
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL WHERE pedido_id=24");
        var blocked = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            db.Repository.EndSessionAsync(db.SessionId, 1, 4, "motoboy_offline", confirmPendingWork: false));
        Assert.Equal("MOTOBOY_HAS_PENDING_WORK", blocked.Code);
        Assert.Equal(0, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_disconnect_incidents"));

        await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "motoboy_offline", confirmPendingWork: true);
        await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "motoboy_offline", confirmPendingWork: true);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_disconnect_incidents"));
        Assert.Equal(2, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_disconnect_incident_items"));
        // Não retirado: volta à loja. Retirado: nada muda (parada, responsável, status e retirada).
        Assert.Equal(1, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=24"));
        Assert.Null(await db.Scalar<int?>("SELECT motoboy_responsavel FROM pedido WHERE id=24"));
        Assert.Equal("removed", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=24"));
        Assert.Equal(2, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=23"));
        Assert.Equal(1, await db.Scalar<int>("SELECT motoboy_responsavel FROM pedido WHERE id=23"));
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
        Assert.True(await db.Scalar<bool>("SELECT collected FROM delivery_motoboy_disconnect_incident_items WHERE pedido_id=23"));
        Assert.False(await db.Scalar<bool>("SELECT collected FROM delivery_motoboy_disconnect_incident_items WHERE pedido_id=24"));
        Assert.Equal("a_receber", await db.Scalar<string>("SELECT payment_snapshot->>'statusPagamento' FROM delivery_motoboy_disconnect_incident_items WHERE pedido_id=23"));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox WHERE event_name='delivery.attention.opened'"));
        Assert.Equal("motoboy_offline", await db.Scalar<string>("SELECT end_reason FROM delivery_motoboy_disconnect_incidents"));
    }

    private static DisconnectIncidentService IncidentService(Database db) =>
        new(new DisconnectIncidentRepository(db.Source), new PedidoQueueRepository(db.Source));

    private static async Task<Database> IncidentDatabase()
    {
        var db = await PresenceDatabase();
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL WHERE pedido_id=24");
        await db.Repository.EndSessionAsync(db.SessionId, 1, 4, "inactivity_7h", confirmPendingWork: true);
        return db;
    }

    private static AcaoIncidenteRequest Act(string action, int version, params int[] pedidos) => new()
    {
        TipoAcao = action, PedidoIds = pedidos.ToList(), VersaoEsperada = version, IdempotencyKey = Guid.NewGuid().ToString(),
        Motivo = "Contato feito por telefone com o motoboy."
    };

    [DeliveryDatabaseFact]
    public async Task IncidentExposesOnlyAllowedActionsAndBlocksCollectedRedistributionUntilPhysicalReturn()
    {
        await using var db = await IncidentDatabase();
        var service = IncidentService(db);
        var list = await service.ListAsync(db.StoreId, false, null, false);
        var incident = Assert.Single(list.Incidentes);
        Assert.Equal("aberto", incident.Status);
        Assert.Equal(2, incident.Pedidos.Count);
        var collected = incident.Pedidos.Single(p => p.PedidoId == 23);
        Assert.True(collected.Coletado);
        Assert.Equal("em_rota", collected.Estagio);
        Assert.Equal("em_rota", collected.StatusPedido);
        Assert.False(collected.Acoes.Single(a => a.Acao == IncidentActions.Redistribuir).Permitida);
        Assert.True(collected.Acoes.Single(a => a.Acao == IncidentActions.ConfirmarDevolucaoFisica).Permitida);
        Assert.False(collected.Acoes.Single(a => a.Acao == IncidentActions.Cancelar).Permitida);
        Assert.True(incident.Pedidos.Single(p => p.PedidoId == 24).Acoes.Single(a => a.Acao == IncidentActions.Redistribuir).Permitida);

        // Redistribuir mercadoria coletada sem devolução é recusado (422) e nada muda.
        var blocked = await Assert.ThrowsAsync<DeliveryDomainException>(() => service.ActAsync(db.StoreId, 9, false, incident.Id,
            WithDestination(Act(IncidentActions.Redistribuir, incident.Version, 23), 2)));
        Assert.Equal("ACAO_NAO_PERMITIDA", blocked.Code);
        Assert.Equal("en_route", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
        // Cancelar o que está com o motoboy só com autorização excepcional.
        var cancelBlocked = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            service.ActAsync(db.StoreId, 9, false, incident.Id, Act(IncidentActions.Cancelar, incident.Version, 23)));
        Assert.Equal("ACAO_NAO_PERMITIDA", cancelBlocked.Code);
        // Justificativa obrigatória.
        var noReason = Act(IncidentActions.Cancelar, incident.Version, 24); noReason.Motivo = " ";
        Assert.Equal("JUSTIFICATIVA_OBRIGATORIA", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            service.ActAsync(db.StoreId, 9, false, incident.Id, noReason))).Code);
        // Outro estabelecimento não enxerga nem age.
        var other = Guid.NewGuid();
        Assert.Empty((await service.ListAsync(other, true, null, true)).Incidentes);
        Assert.Equal("INCIDENT_NOT_FOUND", (await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            service.ActAsync(other, 9, true, incident.Id, Act(IncidentActions.AguardarContato, incident.Version)))).Code);
    }

    private static AcaoIncidenteRequest WithDestination(AcaoIncidenteRequest request, int motoboyId)
    {
        request.MotoboyDestinoId = motoboyId;
        return request;
    }

    [DeliveryDatabaseFact]
    public async Task IncidentActionsAreVersionedIdempotentAndResolveWhenNoOrderIsPending()
    {
        await using var db = await IncidentDatabase();
        var service = IncidentService(db);
        var incident = (await service.ListAsync(db.StoreId, false, null, true)).Incidentes.Single();

        var wait = Act(IncidentActions.AguardarContato, incident.Version);
        var waiting = await service.ActAsync(db.StoreId, 9, true, incident.Id, wait);
        Assert.Equal("aguardando_retorno", waiting.Incidente.Status);
        Assert.True(waiting.Incidente.Version > incident.Version);
        // Repetir a mesma chave devolve o resultado guardado, sem gravar outra vez.
        var replay = await service.ActAsync(db.StoreId, 9, true, incident.Id, wait);
        Assert.Equal(waiting.Incidente.Version, replay.Incidente.Version);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_disconnect_incident_actions"));
        // Versão antiga: 409 com o incidente atual.
        var stale = await Assert.ThrowsAsync<DeliveryDomainException>(() =>
            service.ActAsync(db.StoreId, 9, true, incident.Id, Act(IncidentActions.RegistrarContato, incident.Version)));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal("VERSAO_DESATUALIZADA", stale.Code);
        Assert.IsType<IncidenteAtencaoDto>(stale.Details);

        // Cancelar o não retirado (24). Devolução física do coletado (23) e depois cancelamento autorizado.
        var current = waiting.Incidente.Version;
        var cancel24 = await service.ActAsync(db.StoreId, 9, false, incident.Id, Act(IncidentActions.Cancelar, current, 24));
        Assert.True(cancel24.Resultados.Single().Ok);
        Assert.Equal(4, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=24"));
        Assert.Equal("aguardando_retorno", cancel24.Incidente.Status);
        var returned = await service.ActAsync(db.StoreId, 9, false, incident.Id, Act(IncidentActions.ConfirmarDevolucaoFisica, cancel24.Incidente.Version, 23));
        Assert.True(returned.Resultados.Single().Ok);
        Assert.Equal(1, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=23"));
        Assert.Equal("removed", await db.Scalar<string>("SELECT stop_status FROM delivery_route_stops WHERE pedido_id=23"));
        var item = returned.Incidente.Pedidos.Single(p => p.PedidoId == 23);
        Assert.True(item.DevolucaoConfirmada);
        Assert.Equal("na_loja", item.Estagio);
        Assert.True(item.Acoes.Single(a => a.Acao == IncidentActions.Cancelar).Permitida);
        var done = await service.ActAsync(db.StoreId, 9, false, incident.Id,
            Act(IncidentActions.Cancelar, returned.Incidente.Version, 23));
        Assert.Equal("resolvido", done.Incidente.Status);
        Assert.Equal("Atendente", done.Incidente.ResolvidoPorNome);
        Assert.Equal(2, done.Incidente.Historico.Count(h => h.Acao == IncidentActions.Cancelar));
        Assert.Empty((await service.ListAsync(db.StoreId, false, null, true)).Incidentes);
        Assert.Single((await service.ListAsync(db.StoreId, true, null, true)).Incidentes);
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_realtime_outbox WHERE event_name='delivery.attention.resolved'"));
    }

    [DeliveryDatabaseFact]
    public async Task ConcurrentActionsWithTheSameVersionApplyOnlyOnce()
    {
        await using var db = await IncidentDatabase();
        var service = IncidentService(db);
        var incident = (await service.ListAsync(db.StoreId, false, null, true)).Incidentes.Single();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await service.ActAsync(db.StoreId, 9, true, incident.Id, Act(IncidentActions.Cancelar, incident.Version, 24)); return "ok"; }
            catch (DeliveryDomainException ex) { return ex.Code; }
        }));
        Assert.Equal(1, attempts.Count(a => a == "ok"));
        Assert.All(attempts.Where(a => a != "ok"), code => Assert.Contains(code, new[] { "VERSAO_DESATUALIZADA", "ACAO_EM_ANDAMENTO" }));
        Assert.Equal(1, await db.Scalar<int>("SELECT COUNT(*)::int FROM delivery_motoboy_disconnect_incident_actions WHERE state='concluida'"));
    }

    [DeliveryDatabaseFact]
    public async Task OrderHandledOutsideTheIncidentIsReconciledAndShiftEndRacingCompletionStaysConsistent()
    {
        await using var db = await PresenceDatabase();
        await db.Execute("UPDATE delivery_route_stops SET picked_up_at_utc=NULL WHERE pedido_id=24");
        var queue = new PedidoQueueRepository(db.Source);
        // Conclusão do pedido coletado correndo junto com o encerramento do turno.
        var end = db.Repository.EndSessionAsync(db.SessionId, 1, 4, "client_end", confirmPendingWork: true);
        var complete = Task.Run(async () =>
        {
            try { await queue.CompleteStopAsync(db.StoreId, 9, 23, "Entregue"); return (string?)null; }
            catch (DeliveryDomainException ex) { return ex.Code; }
        });
        await end;
        await complete;
        Assert.Equal(3, await db.Scalar<int>("SELECT status_pedido FROM pedido WHERE id=23"));
        var service = IncidentService(db);
        var incident = Assert.Single((await service.ListAsync(db.StoreId, true, null, true)).Incidentes);
        Assert.Equal("pendente", incident.Pedidos.Single(p => p.PedidoId == 24).Situacao);
        var delivered = incident.Pedidos.SingleOrDefault(p => p.PedidoId == 23);
        if (delivered != null) { Assert.Equal("resolvido", delivered.Situacao); Assert.Equal("pedido_encerrado", delivered.Resolucao); }

        // Pedido 24 é atribuído pelo painel normal: o incidente reconhece e se resolve sozinho.
        await db.Execute("UPDATE pedido SET status_pedido=5, motoboy_responsavel=2 WHERE id=24");
        var reconciled = Assert.Single((await service.ListAsync(db.StoreId, true, null, true)).Incidentes);
        Assert.Equal("resolvido", reconciled.Status);
        Assert.All(reconciled.Pedidos, p => Assert.Equal("resolvido", p.Situacao));
    }

    [DeliveryDatabaseFact]
    public async Task OverdueMobileShiftIsEndedWithTheSameIncidentRules()
    {
        await using var db = await PresenceDatabase();
        await db.Execute("UPDATE motoboy_active_sessions SET expires_at_utc=NOW()-INTERVAL '1 second'; UPDATE delivery_route_stops SET picked_up_at_utc=NULL WHERE pedido_id=24");
        Assert.Equal(1, await db.Repository.ExpireDueSessionsAsync(100));
        Assert.Equal(0, await db.Repository.ExpireDueSessionsAsync(100));
        Assert.Equal("inactivity_7h", await db.Scalar<string>("SELECT end_reason FROM delivery_motoboy_disconnect_incidents"));
        var incident = Assert.Single((await IncidentService(db).ListAsync(db.StoreId, false, null, false)).Incidentes);
        Assert.Equal("inactivity_7h", incident.Motivo);
        Assert.False(incident.Motoboy.Reconectado);
        // Retomada: um novo turno do mesmo motoboy marca reconectado e libera "reofertar".
        await db.Repository.StartMobileSessionAsync(7, db.StoreId, Guid.NewGuid(), "novo-aparelho");
        var again = Assert.Single((await IncidentService(db).ListAsync(db.StoreId, false, null, false)).Incidentes);
        Assert.True(again.Motoboy.Reconectado);
        Assert.True(again.Pedidos.Single(p => p.PedidoId == 23).Acoes.Single(a => a.Acao == IncidentActions.ReofertarMesmoMotoboy).Permitida);
    }
}
