using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using APIBack.Services;
using APIBack.DTOs.Tracking;
using APIBack.Hubs;
using APIBack.Model.Tracking;
using APIBack.Options;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class OperationalSessionRepository : IOperationalSessionRepository
    {
        private const string SessionSelect = @"
SELECT s.session_id AS SessionId,
       s.session_epoch AS SessionEpoch,
       s.motoboy_id AS MotoboyId,
       s.id_usuario AS UsuarioId,
       s.id_estabelecimento AS EstabelecimentoId,
       s.origin AS Origin,
       s.client_instance_id AS ClientInstanceId,
       s.started_by_user_id AS StartedByUserId,
       s.idempotency_key AS IdempotencyKey,
       s.started_at_utc AS StartedAtUtc,
       s.last_heartbeat_at_utc AS LastHeartbeatAtUtc,
       s.expires_at_utc AS ExpiresAtUtc,
       COALESCE(s.ended_at_utc, s.revoked_at) AS EndedAtUtc,
       COALESCE(s.end_reason, s.revoke_reason) AS EndReason,
       s.version AS Version,
       COALESCE(m.nome, '') AS Nome,
       m.avatar AS Avatar,
       COALESCE(m.status, 2) AS Status,
       COALESCE(lc.sequence, 0) + 1 AS NextLocationSequence
  FROM motoboy_active_sessions s
  JOIN motoboy m ON m.id = s.motoboy_id
  LEFT JOIN motoboy_location_current lc ON lc.session_id = s.session_id ";

        private readonly NpgsqlDataSource _dataSource;
        private readonly DeliveryTrackingOptions _options;
        private readonly DeliverySyncMetrics? _metrics;

        public OperationalSessionRepository(
            NpgsqlDataSource dataSource,
            IOptions<DeliveryTrackingOptions> options, DeliverySyncMetrics? metrics = null)
        {
            _metrics = metrics;
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<OperationalSessionRecord> StartMobileSessionAsync(
            int userId,
            Guid estabelecimentoId,
            Guid attemptId,
            string clientInstanceId,
            Guid? expectedSessionId = null,
            bool explicitSwitch = false)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await AcquireAttemptLockAsync(connection, transaction, attemptId);
            await ExpireDueSessionsInternalAsync(connection, transaction, 100);

            var identity = await ResolveMobileIdentityForUpdateAsync(connection, transaction, userId, estabelecimentoId)
                ?? throw new DeliveryDomainException(403, "LINK_FORBIDDEN", "Motoboy sem vinculo ativo com o estabelecimento.");

            var repeated = await GetByAttemptAsync(connection, transaction, userId, "mobile", attemptId);
            if (repeated != null)
            {
                EnsureAttemptScope(repeated, estabelecimentoId);
                EnsureAttemptCanBeReturned(repeated);
                await transaction.CommitAsync();
                return repeated;
            }

            var active = await GetOpenSessionForUpdateAsync(connection, transaction, identity.MotoboyId);
            if (active != null && active.ExpiresAtUtc <= await GetServerNowAsync(connection, transaction))
            {
                await EndLockedSessionAsync(connection, transaction, active, "heartbeat_timeout", null);
                active = null;
            }

            if (active != null)
            {
                if (!explicitSwitch)
                {
                    throw SessionConflict(active);
                }

                if (!expectedSessionId.HasValue || expectedSessionId == Guid.Empty || active.SessionId != expectedSessionId.Value)
                {
                    throw new DeliveryDomainException(409, "SESSION_CHANGED", "A sessao ativa mudou antes da troca explicita.");
                }

                await EndLockedSessionAsync(connection, transaction, active, "explicit_switch", userId);
            }

            var created = await InsertSessionAsync(
                connection,
                transaction,
                identity,
                userId,
                attemptId,
                string.IsNullOrWhiteSpace(clientInstanceId) ? attemptId.ToString("D") : clientInstanceId.Trim(),
                "mobile");
            await transaction.CommitAsync();
            return created;
        }

        public async Task<OperationalSessionRecord> StartSimulatorSessionAsync(
            int actorUserId,
            Guid estabelecimentoId,
            Guid attemptId,
            int? motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await AcquireAttemptLockAsync(connection, transaction, attemptId);
            await ExpireDueSessionsInternalAsync(connection, transaction, 100);

            var repeated = await GetByAttemptAsync(connection, transaction, actorUserId, "simulator", attemptId);
            if (repeated != null)
            {
                EnsureAttemptScope(repeated, estabelecimentoId);
                EnsureAttemptCanBeReturned(repeated);
                await AttachLastKnownLocationAsync(connection, transaction, repeated);
                await transaction.CommitAsync();
                return repeated;
            }

            const string candidateSql = @"
SELECT m.id AS MotoboyId,
       m.id_usuario AS UsuarioId,
       me.estabelecimento_id AS EstabelecimentoId,
       COALESCE(m.nome, '') AS Nome,
       m.avatar AS Avatar,
       COALESCE(m.status, 2) AS Status,
       m.is_simulated AS IsSimulated,
       me.simulator_enabled AS SimulatorEnabled
  FROM motoboy_estabelecimento me
  JOIN motoboy m ON m.id = me.motoboy_id
  JOIN estabelecimentos e ON e.id = me.estabelecimento_id
 WHERE me.estabelecimento_id = @EstabelecimentoId
   AND me.ativo = TRUE
   AND me.simulator_enabled = TRUE
   AND m.is_simulated = TRUE
   AND m.canonical_motoboy_id = m.id
   AND COALESCE(e.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
   -- Sessao de simulador aberta nao bloqueia: ela nao expira sozinha, entao quem reabre a
   -- pagina assume a sessao anterior (o cliente que a abriu pode ter sumido).
   AND NOT EXISTS (
       SELECT 1
         FROM motoboy_active_sessions s
        WHERE s.motoboy_id = m.id
          AND s.ended_at_utc IS NULL
          AND s.revoked_at IS NULL
          AND s.expires_at_utc > NOW()
          AND s.origin <> 'simulator'
   )
   -- Motoboy escolhido pelo operador; sem escolha, o primeiro elegivel.
   AND (@MotoboyId::int IS NULL OR m.id = @MotoboyId)
 ORDER BY me.created_at_utc, me.motoboy_id
 FOR UPDATE OF m SKIP LOCKED
 LIMIT 1;";

            var candidate = await connection.QueryFirstOrDefaultAsync<OperationalMotoboyIdentity>(
                candidateSql,
                new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId },
                transaction);
            if (candidate == null)
            {
                throw motoboyId.HasValue
                    ? new DeliveryDomainException(
                        409,
                        "MOTOBOY_NOT_ELIGIBLE",
                        "Este motoboy simulado nao esta disponivel: ja tem sessao aberta ou nao pertence ao estabelecimento.")
                    : new DeliveryDomainException(
                        409,
                        "NO_ELIGIBLE_MOTOBOY",
                        "Nenhum motoboy simulado esta elegivel para iniciar uma sessao.");
            }

            var staleSession = await GetOpenSessionForUpdateAsync(connection, transaction, candidate.MotoboyId);
            if (staleSession != null)
            {
                if (staleSession.Origin == "simulator")
                {
                    // Nao e "offline por vontade do motoboy": o mesmo simulador reassume a sessao.
                    await EndLockedSessionAsync(connection, transaction, staleSession, "simulator_takeover", actorUserId);
                }
                else
                {
                    if (staleSession.ExpiresAtUtc > await GetServerNowAsync(connection, transaction))
                    {
                        throw SessionConflict(staleSession);
                    }

                    await EndLockedSessionAsync(connection, transaction, staleSession, "heartbeat_timeout", null);
                }
            }

            var created = await InsertSessionAsync(
                connection,
                transaction,
                candidate,
                actorUserId,
                attemptId,
                attemptId.ToString("D"),
                "simulator");
            await AttachLastKnownLocationAsync(connection, transaction, created);
            await transaction.CommitAsync();
            return created;
        }

        /// <summary>
        /// A posicao do motoboy simulado so muda quando o operador o move. Uma sessao nova nao
        /// pode "esquecer" onde ele estava (e o simulador recolocaria o pino no restaurante).
        /// </summary>
        private static async Task AttachLastKnownLocationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            OperationalSessionRecord session)
        {
            var last = await connection.QueryFirstOrDefaultAsync<LastKnownLocationRow>(@"
SELECT latitude AS Latitude, longitude AS Longitude
  FROM motoboy_location_current
 WHERE motoboy_id = @MotoboyId
 ORDER BY received_at_utc DESC
 LIMIT 1;", new { session.MotoboyId }, transaction);
            if (last == null) return;
            session.LastKnownLatitude = last.Latitude;
            session.LastKnownLongitude = last.Longitude;
        }

        private sealed class LastKnownLocationRow
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
        }

        public async Task<OperationalSessionRecord?> GetSessionAsync(Guid sessionId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<OperationalSessionRecord>(
                SessionSelect + " WHERE s.session_id = @SessionId LIMIT 1;",
                new { SessionId = sessionId });
        }

        public async Task<OperationalSessionRecord> HeartbeatAsync(
            Guid sessionId, int motoboyId, long sessionEpoch, CancellationToken cancellationToken = default)
        {
            using var measurement = _metrics?.MeasureDatabase("heartbeat");
            await using var connection = await OpenSyncConnectionAsync("heartbeat", cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // UPDATE RETURNING e a leitura dos dados para o JWT viajam juntos ao banco.
            // NOW() usa o relogio do PostgreSQL e o UPDATE mantem a validacao atomica.
            var sql = @"
WITH updated AS (
    UPDATE motoboy_active_sessions s
       SET last_heartbeat_at_utc = NOW(), last_seen_at = NOW(),
           expires_at_utc = NOW() + (CASE WHEN s.origin = 'simulator' THEN @SimulatorTtl ELSE @Ttl END * INTERVAL '1 second'),
           version = s.version + 1
     WHERE s.session_id = @SessionId AND s.motoboy_id = @MotoboyId AND s.session_epoch = @SessionEpoch
       AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW()
       AND EXISTS (
           SELECT 1 FROM motoboy_estabelecimento me
           JOIN estabelecimentos e ON e.id = me.estabelecimento_id
           JOIN motoboy m ON m.id = me.motoboy_id
           WHERE me.motoboy_id = s.motoboy_id AND me.estabelecimento_id = s.id_estabelecimento
             AND me.ativo = TRUE AND COALESCE(e.ativo, TRUE) = TRUE
             AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
             AND ((s.origin = 'simulator' AND me.simulator_enabled = TRUE AND m.is_simulated = TRUE)
                  OR (s.origin = 'mobile' AND EXISTS (
                      SELECT 1 FROM usuario_estabelecimentos ue
                       WHERE ue.id_usuario = s.id_usuario AND ue.id_estabelecimento = s.id_estabelecimento
                         AND LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
                         AND COALESCE(ue.ativo, TRUE) = TRUE AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'))))
    RETURNING s.*
) " + SessionSelect.Replace("FROM motoboy_active_sessions s", "FROM updated s");
            var session = await connection.QuerySingleOrDefaultAsync<OperationalSessionRecord>(new CommandDefinition(sql,
                new { SessionId = sessionId, MotoboyId = motoboyId, SessionEpoch = sessionEpoch,
                    Ttl = _options.PresenceTtlSeconds, SimulatorTtl = PresenceTtlSecondsFor("simulator") },
                transaction, commandTimeout: 10, cancellationToken: cancellationToken))
                ?? throw new DeliveryDomainException(401, "SESSION_EXPIRED", "Sessao operacional encerrada ou expirada.");
            await InsertRealtimeEventAsync(connection, transaction, DeliveryRealtimeEvents.MotoboyStatusChanged,
                "presence.heartbeat", session, new { status = "online", presenceExpiresAtUtc = session.ExpiresAtUtc }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            measurement?.Complete();
            return session;
        }

        public async Task<OperationalSessionRecord> EndSessionAsync(
            Guid sessionId,
            int motoboyId,
            long sessionEpoch,
            string reason)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var session = await GetSessionForUpdateAsync(connection, transaction, sessionId)
                ?? throw new DeliveryDomainException(401, "SESSION_EXPIRED", "Sessao operacional nao encontrada.");
            if (session.MotoboyId != motoboyId || session.SessionEpoch != sessionEpoch)
            {
                throw new DeliveryDomainException(409, "SESSION_CHANGED", "Token nao corresponde a sessao informada.");
            }

            if (!session.EndedAtUtc.HasValue)
            {
                var hasPendingWork = await connection.ExecuteScalarAsync<bool>(@"
SELECT
    EXISTS (
        SELECT 1
          FROM delivery_route_stops rs
         WHERE rs.motoboy_id = @MotoboyId
           AND rs.stop_status IN ('assigned', 'en_route')
    )
    OR EXISTS (
        SELECT 1
          FROM delivery_transfer_requests tr
         WHERE tr.status = 'pending_approval'
           AND (tr.from_motoboy_id = @MotoboyId OR tr.to_motoboy_id = @MotoboyId)
    );",
                    new { MotoboyId = motoboyId },
                    transaction);
                if (hasPendingWork)
                {
                    throw new DeliveryDomainException(
                        409,
                        "MOTOBOY_HAS_PENDING_WORK",
                        "O motoboy possui pedidos em rota, na fila ou transferencias pendentes e nao pode ficar offline.");
                }

                session = await EndLockedSessionAsync(
                    connection,
                    transaction,
                    session,
                    string.IsNullOrWhiteSpace(reason) ? "client_end" : reason.Trim(),
                    session.StartedByUserId);
            }

            await transaction.CommitAsync();
            return session;
        }

        public async Task<OperationalLocationWriteResult> WriteLocationAsync(
            Guid sessionId, int motoboyId, long sessionEpoch, OperationalLocationWrite location,
            CancellationToken cancellationToken = default) =>
            (await WriteLocationsCoreAsync(sessionId, motoboyId, sessionEpoch, new[] { location }, true, cancellationToken))[0];

        public Task<IReadOnlyList<OperationalLocationWriteResult>> WriteLocationsAsync(
            Guid sessionId, int motoboyId, long sessionEpoch, IReadOnlyList<OperationalLocationWrite> locations,
            CancellationToken cancellationToken = default) =>
            WriteLocationsCoreAsync(sessionId, motoboyId, sessionEpoch, locations, false, cancellationToken);

        private async Task<IReadOnlyList<OperationalLocationWriteResult>> WriteLocationsCoreAsync(
            Guid sessionId, int motoboyId, long sessionEpoch, IReadOnlyList<OperationalLocationWrite> locations,
            bool rejectStale, CancellationToken cancellationToken)
        {
            if (locations.Count is < 1 or > OperationalLocationBatchRules.MaxSamples)
                throw new DeliveryDomainException(422, "LOCATION_BATCH_INVALID", "Quantidade de localizacoes invalida.");
            using var measurement = _metrics?.MeasureDatabase("location.write");
            await using var connection = await OpenSyncConnectionAsync("location", cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var serverNow = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition("SELECT NOW();",
                transaction: transaction, commandTimeout: 10, cancellationToken: cancellationToken));
            var session = await GetSessionForUpdateAsync(connection, transaction, sessionId, cancellationToken)
                ?? throw new DeliveryDomainException(401, "SESSION_EXPIRED", "Sessao operacional nao encontrada.");
            if (session.MotoboyId != motoboyId || session.SessionEpoch != sessionEpoch ||
                session.EndedAtUtc.HasValue || session.ExpiresAtUtc <= serverNow)
                throw new DeliveryDomainException(401, "SESSION_EXPIRED", "Sessao operacional encerrada ou expirada.");
            var hasActiveLink = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (
    SELECT 1 FROM motoboy_estabelecimento me
    JOIN estabelecimentos e ON e.id = me.estabelecimento_id
    JOIN motoboy m ON m.id = me.motoboy_id
    WHERE me.motoboy_id = @MotoboyId AND me.estabelecimento_id = @EstabelecimentoId
      AND me.ativo = TRUE AND COALESCE(e.ativo, TRUE) = TRUE
      AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
      AND ((@Origin = 'simulator' AND me.simulator_enabled = TRUE AND m.is_simulated = TRUE)
           OR (@Origin = 'mobile' AND EXISTS (
               SELECT 1 FROM usuario_estabelecimentos ue
                WHERE ue.id_usuario = @UsuarioId AND ue.id_estabelecimento = @EstabelecimentoId
                  AND LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
                  AND COALESCE(ue.ativo, TRUE) = TRUE AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'))));",
                new { MotoboyId = motoboyId, session.EstabelecimentoId, session.Origin, session.UsuarioId },
                transaction, commandTimeout: 10, cancellationToken: cancellationToken));
            if (!hasActiveLink)
                throw new DeliveryDomainException(403, "LINK_FORBIDDEN", "Vinculo operacional foi desativado.");

            // Consultar todas as identidades e a posicao corrente uma vez, com a sessao bloqueada.
            using var reader = await connection.QueryMultipleAsync(new CommandDefinition(@"
SELECT sample_id AS SampleId, sequence AS Sequence, payload_hash AS PayloadHash,
       received_at_utc AS ReceivedAtUtc, updated_current AS UpdatedCurrent
  FROM motoboy_location_samples WHERE session_id = @SessionId
   AND (sample_id = ANY(@SampleIds) OR sequence = ANY(@Sequences));
SELECT sequence FROM motoboy_location_current WHERE session_id = @SessionId FOR UPDATE;",
                new { SessionId = sessionId, SampleIds = locations.Select(l => l.SampleId).ToArray(),
                    Sequences = locations.Select(l => l.Sequence).ToArray() },
                transaction, commandTimeout: 10, cancellationToken: cancellationToken));
            var known = (await reader.ReadAsync<StoredLocationSample>()).ToArray();
            var currentSequence = await reader.ReadSingleOrDefaultAsync<long?>() ?? 0;
            var results = OperationalLocationBatchRules.Plan(locations, known, currentSequence, session.Version, serverNow, rejectStale);
            var accepted = locations.Where((_, i) => results[i].Outcome == "accepted").ToArray();
            if (accepted.Length > 0)
            {
                var latest = accepted[^1];
                // Uma transacao, um incremento e um evento por lote. Todos os pontos novos
                // ficam no historico; somente o ultimo substitui a localizacao corrente.
                session.Version = await connection.QuerySingleAsync<long>(new CommandDefinition(@"
WITH changed_session AS (
    UPDATE motoboy_active_sessions SET last_seen_at = @ReceivedAtUtc, version = version + 1
     WHERE session_id = @SessionId RETURNING version
), inserted_samples AS (
    INSERT INTO motoboy_location_samples (
        sample_id, session_id, motoboy_id, estabelecimento_id, sequence, latitude, longitude,
        accuracy_meters, speed_mps, heading_degrees, tracking_mode, quality,
        captured_at_utc, received_at_utc, payload_hash, updated_current)
    SELECT x.sample_id, @SessionId, @MotoboyId, @EstabelecimentoId, x.sequence, x.latitude, x.longitude,
           x.accuracy, x.speed, x.heading, x.mode, x.quality, x.captured, @ReceivedAtUtc, x.hash,
           x.sequence = @Sequence
      FROM UNNEST(@SampleIds::uuid[], @Sequences::bigint[], @Latitudes::double precision[], @Longitudes::double precision[],
                  @Accuracies::double precision[], @Speeds::double precision[], @Headings::double precision[],
                  @Modes::text[], @Qualities::text[], @Captured::timestamptz[], @Hashes::text[])
           AS x(sample_id, sequence, latitude, longitude, accuracy, speed, heading, mode, quality, captured, hash)
    RETURNING id
), changed_current AS (
    INSERT INTO motoboy_location_current (
        session_id, motoboy_id, estabelecimento_id, sample_id, sequence, latitude, longitude,
        accuracy_meters, speed_mps, heading_degrees, tracking_mode, quality, captured_at_utc, received_at_utc, version)
    VALUES (@SessionId, @MotoboyId, @EstabelecimentoId, @SampleId, @Sequence, @Latitude, @Longitude,
        @AccuracyMeters, @SpeedMps, @HeadingDegrees, @TrackingMode, @Quality, @CapturedAtUtc, @ReceivedAtUtc, 1)
    ON CONFLICT (session_id) DO UPDATE SET
        sample_id = EXCLUDED.sample_id, sequence = EXCLUDED.sequence,
        latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude,
        accuracy_meters = EXCLUDED.accuracy_meters, speed_mps = EXCLUDED.speed_mps,
        heading_degrees = EXCLUDED.heading_degrees, tracking_mode = EXCLUDED.tracking_mode,
        quality = EXCLUDED.quality, captured_at_utc = EXCLUDED.captured_at_utc,
        received_at_utc = EXCLUDED.received_at_utc, version = motoboy_location_current.version + 1
    WHERE motoboy_location_current.sequence < EXCLUDED.sequence
    RETURNING version
)
SELECT version FROM changed_session;",
                    new { SessionId = sessionId, MotoboyId = motoboyId, session.EstabelecimentoId, ReceivedAtUtc = serverNow,
                        SampleIds = accepted.Select(l => l.SampleId).ToArray(), Sequences = accepted.Select(l => l.Sequence).ToArray(),
                        Latitudes = accepted.Select(l => l.Latitude).ToArray(), Longitudes = accepted.Select(l => l.Longitude).ToArray(),
                        Accuracies = accepted.Select(l => l.AccuracyMeters).ToArray(), Speeds = accepted.Select(l => l.SpeedMps).ToArray(),
                        Headings = accepted.Select(l => l.HeadingDegrees).ToArray(), Modes = accepted.Select(l => l.TrackingMode).ToArray(),
                        Qualities = accepted.Select(l => l.Quality).ToArray(), Captured = accepted.Select(l => l.CapturedAtUtc).ToArray(),
                        Hashes = accepted.Select(l => l.PayloadHash).ToArray(), latest.SampleId, latest.Sequence, latest.Latitude,
                        latest.Longitude, latest.AccuracyMeters, latest.SpeedMps, latest.HeadingDegrees,
                        latest.TrackingMode, latest.Quality, latest.CapturedAtUtc },
                    transaction, commandTimeout: 10, cancellationToken: cancellationToken));
                await InsertRealtimeEventAsync(connection, transaction, DeliveryRealtimeEvents.MotoboyLocationUpdated,
                    "location.updated", session, new { sampleId = latest.SampleId, sequence = latest.Sequence,
                        latitude = latest.Latitude, longitude = latest.Longitude, accuracyMeters = latest.AccuracyMeters,
                        speedMps = latest.SpeedMps, headingDegrees = latest.HeadingDegrees, trackingMode = latest.TrackingMode,
                        quality = latest.Quality, capturedAtUtc = latest.CapturedAtUtc, receivedAtUtc = serverNow }, cancellationToken);
            }
            foreach (var result in results) result.SessionVersion = session.Version;
            await transaction.CommitAsync(cancellationToken);
            measurement?.Complete();
            foreach (var group in results.GroupBy(r => r.Outcome)) _metrics?.RecordLocations(group.Key, group.Count());
            return results;
        }

        private async Task<NpgsqlConnection> OpenSyncConnectionAsync(string operation, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try { return await _dataSource.OpenConnectionAsync(cancellationToken); }
            finally { _metrics?.RecordPoolWait(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        }

        public async Task<DeliveryTrackingSnapshotDto> GetSnapshotAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var serverNow = await connection.ExecuteScalarAsync<DateTimeOffset>("SELECT NOW();");
            const string sql = @"
SELECT s.motoboy_id AS MotoboyId,
       COALESCE(m.nome, '') AS Nome,
       m.avatar AS Avatar,
       s.session_id AS SessionId,
       s.session_epoch AS SessionEpoch,
       s.version AS Version,
       s.origin AS Origin,
       s.expires_at_utc AS PresenceExpiresAtUtc,
       lc.latitude AS Latitude,
       lc.longitude AS Longitude,
       lc.accuracy_meters AS AccuracyMeters,
       lc.speed_mps AS SpeedMps,
       lc.heading_degrees AS HeadingDegrees,
       lc.tracking_mode AS TrackingMode,
       lc.quality AS Quality,
       lc.sequence AS Sequence,
       lc.captured_at_utc AS CapturedAtUtc,
       lc.received_at_utc AS ReceivedAtUtc,
       EXISTS(
           SELECT 1
             FROM delivery_route_stops rs
            WHERE rs.motoboy_id = s.motoboy_id
              AND rs.estabelecimento_id = s.id_estabelecimento
              AND rs.stop_status = 'en_route'
       ) AS HasActiveRouteStop
  FROM motoboy_active_sessions s
  JOIN motoboy m ON m.id = s.motoboy_id
  JOIN motoboy_estabelecimento me
    ON me.motoboy_id = s.motoboy_id
   AND me.estabelecimento_id = s.id_estabelecimento
   AND me.ativo = TRUE
  JOIN estabelecimentos e
    ON e.id = me.estabelecimento_id
   AND COALESCE(e.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
  -- Ultima posicao do motoboy em qualquer sessao: uma sessao nova (reabrir o simulador)
  -- nao pode deixar o motoboy sem posicao ate o primeiro envio.
  LEFT JOIN LATERAL (
      SELECT c.*
        FROM motoboy_location_current c
       WHERE c.motoboy_id = s.motoboy_id
       ORDER BY c.received_at_utc DESC
       LIMIT 1
  ) lc ON TRUE
 WHERE s.id_estabelecimento = @EstabelecimentoId
   AND s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
   AND s.expires_at_utc > @ServerNow
   AND (
       (s.origin = 'simulator' AND me.simulator_enabled = TRUE AND m.is_simulated = TRUE)
       OR (
           s.origin = 'mobile'
       )
   )
 ORDER BY m.nome, m.id;";
            var rows = (await connection.QueryAsync<SnapshotRow>(sql, new
            {
                EstabelecimentoId = estabelecimentoId,
                ServerNow = serverNow
            })).ToList();
            var returning = await GetReturningMotoboyIdsAsync(connection, estabelecimentoId);

            var motoboys = rows.Select(row =>
            {
                // O motoboy simulado fica onde o operador o deixou: a posicao nao "envelhece".
                // No app real ela vem do GPS do aparelho, entao a janela de frescor continua valendo.
                var fresh = row.ReceivedAtUtc.HasValue &&
                            (string.Equals(row.Origin, "simulator", StringComparison.Ordinal) ||
                             DeliveryTrackingPolicy.IsLocationFresh(
                                 row.ReceivedAtUtc.Value,
                                 serverNow,
                                 _options.LocationFreshnessSeconds));
                return new OnlineMotoboyDto
                {
                    MotoboyId = row.MotoboyId,
                    Nome = row.Nome,
                    Avatar = row.Avatar,
                    // A fila (delivery_route_stops) e a fonte de verdade sobre estar em rota.
                    // O tracking_mode e declarado pelo cliente e serve apenas como reforco:
                    // sozinho ele deixava o motoboy como "online" mesmo com entrega em rota.
                    Status = row.HasActiveRouteStop
                             || string.Equals(row.TrackingMode, "active_route", StringComparison.OrdinalIgnoreCase)
                        ? "delivering"
                        // Voltando a loja depois da ultima entrega (Fase 4): so quando o estabelecimento exige o retorno.
                        : returning.Contains(row.MotoboyId) ? "returning" : "online",
                    SessionId = row.SessionId,
                    SessionEpoch = row.SessionEpoch,
                    Version = row.Version,
                    Origin = row.Origin,
                    PresenceExpiresAtUtc = row.PresenceExpiresAtUtc,
                    HasRecentLocation = fresh,
                    Location = fresh && row.Latitude.HasValue && row.Longitude.HasValue
                        ? new OperationalLocationDto
                        {
                            Latitude = row.Latitude.Value,
                            Longitude = row.Longitude.Value,
                            AccuracyMeters = row.AccuracyMeters,
                            SpeedMps = row.SpeedMps,
                            HeadingDegrees = row.HeadingDegrees,
                            TrackingMode = row.TrackingMode ?? "online_idle",
                            Quality = row.Quality ?? "unknown",
                            Sequence = row.Sequence ?? 0,
                            CapturedAtUtc = row.CapturedAtUtc ?? serverNow,
                            ReceivedAtUtc = row.ReceivedAtUtc!.Value
                        }
                        : null
                };
            }).ToArray();

            return new DeliveryTrackingSnapshotDto
            {
                EstablishmentId = estabelecimentoId,
                ServerTimeUtc = serverNow,
                Motoboys = motoboys
            };
        }

        /// <summary>Motoboys com a rota em "retornando". Tolera o banco sem a migration da Fase 4 (coluna ausente = ninguem).</summary>
        private static async Task<HashSet<int>> GetReturningMotoboyIdsAsync(NpgsqlConnection connection, Guid estabelecimentoId)
        {
            try
            {
                var ids = await connection.QueryAsync<int>(
                    "SELECT motoboy_id FROM delivery_motoboy_route WHERE estabelecimento_id = @EstabelecimentoId AND route_state = 'returning';",
                    new { EstabelecimentoId = estabelecimentoId });
                return ids.ToHashSet();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return new HashSet<int>();
            }
        }

        public async Task<IReadOnlyCollection<SimulatorCandidateDto>> GetSimulatorCandidatesAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            const string sql = @"
SELECT m.id AS MotoboyId,
       COALESCE(m.nome, '') AS Nome,
       m.avatar AS Avatar,
       m.telefone AS Telefone,
       m.placa_moto AS PlacaMoto,
       m.marca_moto AS MarcaMoto,
       m.modelo_moto AS ModeloMoto,
       me.simulator_enabled AS SimulatorEnabled,
       -- Sessao de outro aparelho (app do motoboy) bloqueia. Sessao de simulador NAO: a
       -- abertura de sessao (StartSimulatorSessionAsync) assume a anterior, porque quem a abriu
       -- pode ter fechado a pagina sem encerra-la. A lista precisa dizer o mesmo que a abertura.
       CASE WHEN blocking.session_id IS NULL THEN TRUE ELSE FALSE END AS Eligible,
       CASE WHEN blocking.session_id IS NULL THEN NULL ELSE 'active_session' END AS UnavailableReason,
       (previous.session_id IS NOT NULL) AS TakesOverSession
  FROM motoboy_estabelecimento me
  JOIN motoboy m ON m.id = me.motoboy_id
  LEFT JOIN motoboy_active_sessions blocking
    ON blocking.motoboy_id = m.id
   AND blocking.ended_at_utc IS NULL
   AND blocking.revoked_at IS NULL
   AND blocking.expires_at_utc > NOW()
   AND blocking.origin <> 'simulator'
  LEFT JOIN motoboy_active_sessions previous
    ON previous.motoboy_id = m.id
   AND previous.ended_at_utc IS NULL
   AND previous.revoked_at IS NULL
   AND previous.expires_at_utc > NOW()
   AND previous.origin = 'simulator'
 WHERE me.estabelecimento_id = @EstabelecimentoId
   AND me.ativo = TRUE
   AND me.simulator_enabled = TRUE
   AND m.is_simulated = TRUE
   AND m.canonical_motoboy_id = m.id
 ORDER BY me.created_at_utc, me.motoboy_id;";
            return (await connection.QueryAsync<SimulatorCandidateDto>(sql, new
            {
                EstabelecimentoId = estabelecimentoId
            })).ToArray();
        }

        public async Task<OperationalMotoboyIdentity> CreateSimulatorMotoboyAsync(
            Guid estabelecimentoId,
            string nome,
            string? telefone)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var motoboyId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO motoboy (nome, telefone, status, id_estabelecimento, is_simulated, compartilhar_localizacao_cliente)
VALUES (@Nome, @Telefone, 2, @EstabelecimentoId, TRUE, TRUE)
RETURNING id;", new
            {
                Nome = nome,
                Telefone = telefone,
                EstabelecimentoId = estabelecimentoId
            }, transaction);
            await connection.ExecuteAsync(@"
UPDATE motoboy
   SET canonical_motoboy_id = id
 WHERE id = @MotoboyId;

INSERT INTO motoboy_estabelecimento (
    motoboy_id, estabelecimento_id, ativo, simulator_enabled, created_at_utc, updated_at_utc)
VALUES (@MotoboyId, @EstabelecimentoId, TRUE, TRUE, NOW(), NOW());",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId },
                transaction);
            await transaction.CommitAsync();

            return new OperationalMotoboyIdentity
            {
                MotoboyId = motoboyId,
                EstabelecimentoId = estabelecimentoId,
                Nome = nome,
                Status = 2,
                IsSimulated = true,
                SimulatorEnabled = true
            };
        }

        public async Task<bool> UpdateSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId, string nome, string? telefone, string? avatar = null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            // So motoboy de teste (is_simulated) vinculado a este estabelecimento: nunca um real.
            var affected = await connection.ExecuteAsync(@"
UPDATE motoboy m
   SET nome = @Nome,
       telefone = @Telefone,
       avatar = CASE WHEN @AvatarSet THEN NULLIF(@Avatar, '') ELSE avatar END
 WHERE m.id = @MotoboyId
   AND m.is_simulated = TRUE
   AND EXISTS (SELECT 1 FROM motoboy_estabelecimento me
                WHERE me.motoboy_id = m.id AND me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE);",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId, Nome = nome, Telefone = telefone, Avatar = avatar, AvatarSet = avatar != null });
            return affected > 0;
        }

        public async Task<SimulatorMotoboyRemoval> RemoveSimulatorMotoboyAsync(Guid estabelecimentoId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var isTestMotoboy = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM motoboy m
      JOIN motoboy_estabelecimento me ON me.motoboy_id = m.id
     WHERE m.id = @MotoboyId AND m.is_simulated = TRUE
       AND me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE);",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            if (!isTestMotoboy) return SimulatorMotoboyRemoval.NotFound;

            // Atribuido (5) ou em rota (2): tirar o motoboy deixaria o pedido orfao.
            var hasActiveOrders = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM pedido
     WHERE motoboy_responsavel = @MotoboyId
       AND id_estabelecimento = @EstabelecimentoId
       AND status_pedido IN (2, 5, 8));",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            if (hasActiveOrders) return SimulatorMotoboyRemoval.HasActiveOrders;

            await connection.ExecuteAsync(@"
UPDATE motoboy_active_sessions
   SET ended_at_utc = NOW(), end_reason = 'simulator_motoboy_removed', revoked_at = NOW(),
       revoke_reason = 'simulator_motoboy_removed', version = version + 1
 WHERE motoboy_id = @MotoboyId AND ended_at_utc IS NULL AND revoked_at IS NULL;

-- Soft: o historico de pedidos e amostras de GPS continua apontando para o motoboy.
UPDATE motoboy_estabelecimento
   SET ativo = FALSE, simulator_enabled = FALSE, updated_at_utc = NOW()
 WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            await transaction.CommitAsync();
            return SimulatorMotoboyRemoval.Removed;
        }

        public async Task<bool> CanUserManageEstablishmentAsync(int userId, bool isSuperAdmin, Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1
      FROM estabelecimentos e
     WHERE e.id = @EstabelecimentoId
       AND COALESCE(e.ativo, TRUE) = TRUE
       AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
       AND (
           @IsSuperAdmin
           OR EXISTS (
               SELECT 1
                 FROM usuario_estabelecimentos ue
                WHERE ue.id_usuario = @UserId
                  AND ue.id_estabelecimento = e.id
                  AND COALESCE(ue.ativo, TRUE) = TRUE
                  AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
                  AND LOWER(COALESCE(ue.tipo_acesso, '')) <> 'motoboy'
           )
       )
);", new { UserId = userId, IsSuperAdmin = isSuperAdmin, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetTrajectoryAsync(
            Guid estabelecimentoId, int motoboyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<MotoboyLocationHistoryPointDto>(@"
SELECT motoboy_id AS MotoboyId,
       latitude AS Latitude,
       longitude AS Longitude,
       accuracy_meters AS AccuracyMeters,
       speed_mps AS SpeedMps,
       heading_degrees AS HeadingDegrees,
       tracking_mode AS TrackingMode,
       quality AS Quality,
       captured_at_utc AS ClientTimestampUtc,
       received_at_utc AS ServerReceivedAtUtc
  FROM motoboy_location_samples
 WHERE estabelecimento_id = @EstabelecimentoId
   AND motoboy_id = @MotoboyId
   AND captured_at_utc >= @FromUtc
   AND captured_at_utc < @ToUtc
 ORDER BY captured_at_utc, sequence
 LIMIT @Limit;",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    MotoboyId = motoboyId,
                    FromUtc = fromUtc,
                    ToUtc = toUtc,
                    Limit = Math.Clamp(limit, 1, 20000)
                })).ToArray();
        }

        private int PresenceTtlSecondsFor(string origin) =>
            origin == "simulator"
                ? Math.Max(_options.PresenceTtlSeconds, _options.SimulatorPresenceTtlSeconds)
                : _options.PresenceTtlSeconds;

        public async Task<int> ExpireDueSessionsAsync(int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var count = await ExpireDueSessionsInternalAsync(connection, transaction, limit);
            await transaction.CommitAsync();
            return count;
        }

        public async Task<int> EndActiveMobileSessionsForUserAsync(int userId, string reason)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var sessions = (await connection.QueryAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.started_by_user_id = @UserId
   AND s.origin = 'mobile'
   AND s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
 FOR UPDATE OF s;",
                new { UserId = userId },
                transaction)).ToList();
            foreach (var session in sessions)
            {
                await EndLockedSessionAsync(connection, transaction, session, reason, userId);
            }

            await transaction.CommitAsync();
            return sessions.Count;
        }

        public async Task<int> DeleteOldLocationsAsync(DateTimeOffset receivedBeforeUtc, int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
DELETE FROM motoboy_location_samples
 WHERE id IN (
     SELECT id
       FROM motoboy_location_samples
      WHERE received_at_utc < @ReceivedBeforeUtc
      ORDER BY received_at_utc
      LIMIT @Limit
 );", new { ReceivedBeforeUtc = receivedBeforeUtc, Limit = Math.Clamp(limit, 1, 10000) });
        }

        private async Task<OperationalMotoboyIdentity?> ResolveMobileIdentityForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int userId,
            Guid estabelecimentoId)
        {
            const string sql = @"
SELECT canonical.id AS MotoboyId,
       @UserId AS UsuarioId,
       me.estabelecimento_id AS EstabelecimentoId,
       COALESCE(canonical.nome, '') AS Nome,
       canonical.avatar AS Avatar,
       COALESCE(canonical.status, 2) AS Status,
       canonical.is_simulated AS IsSimulated,
       me.simulator_enabled AS SimulatorEnabled
  FROM motoboy alias
  JOIN motoboy canonical ON canonical.id = alias.canonical_motoboy_id
  JOIN motoboy_estabelecimento me
    ON me.motoboy_id = canonical.id
   AND me.estabelecimento_id = @EstabelecimentoId
   AND me.ativo = TRUE
  JOIN estabelecimentos e
    ON e.id = me.estabelecimento_id
   AND COALESCE(e.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
 WHERE alias.id_usuario = @UserId
   AND canonical.is_simulated = FALSE
 ORDER BY canonical.id
 FOR UPDATE OF canonical
 LIMIT 1;";
            return await connection.QueryFirstOrDefaultAsync<OperationalMotoboyIdentity>(sql, new
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            }, transaction);
        }

        private async Task<OperationalSessionRecord?> GetByAttemptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int actorUserId,
            string origin,
            Guid attemptId)
        {
            return await connection.QueryFirstOrDefaultAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.started_by_user_id = @ActorUserId
   AND s.origin = @Origin
   AND s.idempotency_key = @AttemptId
 LIMIT 1
 FOR UPDATE OF s;",
                new { ActorUserId = actorUserId, Origin = origin, AttemptId = attemptId },
                transaction);
        }

        private async Task<OperationalSessionRecord?> GetOpenSessionForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int motoboyId)
        {
            return await connection.QueryFirstOrDefaultAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.motoboy_id = @MotoboyId
   AND s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
 LIMIT 1
 FOR UPDATE OF s;",
                new { MotoboyId = motoboyId },
                transaction);
        }

        private async Task<OperationalSessionRecord?> GetSessionForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid sessionId, CancellationToken cancellationToken = default)
        {
            return await connection.QueryFirstOrDefaultAsync<OperationalSessionRecord>(new CommandDefinition(
                SessionSelect + " WHERE s.session_id = @SessionId LIMIT 1 FOR UPDATE OF s;",
                new { SessionId = sessionId }, transaction, commandTimeout: 10, cancellationToken: cancellationToken));
        }

        private async Task<OperationalSessionRecord> InsertSessionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            OperationalMotoboyIdentity identity,
            int actorUserId,
            Guid attemptId,
            string clientInstanceId,
            string origin)
        {
            var serverNow = await GetServerNowAsync(connection, transaction);
            var sessionId = Guid.NewGuid();
            var epoch = await connection.ExecuteScalarAsync<long>(
                "SELECT nextval('motoboy_session_epoch_seq');",
                transaction: transaction);
            var session = new OperationalSessionRecord
            {
                SessionId = sessionId,
                SessionEpoch = epoch,
                MotoboyId = identity.MotoboyId,
                UsuarioId = identity.UsuarioId,
                EstabelecimentoId = identity.EstabelecimentoId,
                Origin = origin,
                ClientInstanceId = clientInstanceId,
                StartedByUserId = actorUserId,
                IdempotencyKey = attemptId,
                StartedAtUtc = serverNow,
                LastHeartbeatAtUtc = serverNow,
                ExpiresAtUtc = serverNow.AddSeconds(PresenceTtlSecondsFor(origin)),
                Version = 1,
                Nome = identity.Nome,
                Avatar = identity.Avatar,
                Status = identity.Status
            };

            await connection.ExecuteAsync(@"
INSERT INTO motoboy_active_sessions (
    session_id, session_epoch, contract_version, motoboy_id, id_usuario, id_estabelecimento,
    device_type, origin, client_instance_id, started_by_user_id, idempotency_key,
    created_at, started_at_utc, last_seen_at, last_heartbeat_at_utc, expires_at_utc, version)
VALUES (
    @SessionId, @SessionEpoch, 2, @MotoboyId, @UsuarioId, @EstabelecimentoId,
    @Origin, @Origin, @ClientInstanceId, @StartedByUserId, @IdempotencyKey,
    @StartedAtUtc, @StartedAtUtc, @LastHeartbeatAtUtc, @LastHeartbeatAtUtc, @ExpiresAtUtc, @Version);",
                session,
                transaction);
            await InsertAuditEventAsync(connection, transaction, session, "session.started", actorUserId, null);
            await InsertRealtimeEventAsync(
                connection,
                transaction,
                DeliveryRealtimeEvents.MotoboyStatusChanged,
                "presence.online",
                session,
                new { status = "online", origin, presenceExpiresAtUtc = session.ExpiresAtUtc });
            return session;
        }

        private async Task<OperationalSessionRecord> EndLockedSessionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            OperationalSessionRecord session,
            string reason,
            int? actorUserId)
        {
            var serverNow = await GetServerNowAsync(connection, transaction);
            session = await connection.QuerySingleAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.session_id = (
     SELECT session_id
       FROM motoboy_active_sessions
      WHERE session_id = @SessionId
        AND ended_at_utc IS NULL
        AND revoked_at IS NULL
      FOR UPDATE
 )", new { session.SessionId }, transaction);

            await connection.ExecuteAsync(@"
UPDATE motoboy_active_sessions
   SET ended_at_utc = @ServerNow,
       end_reason = @Reason,
       revoked_at = @ServerNow,
       revoke_reason = @Reason,
       version = version + 1
 WHERE session_id = @SessionId;",
                new { session.SessionId, ServerNow = serverNow, Reason = reason },
                transaction);
            session.EndedAtUtc = serverNow;
            session.EndReason = reason;
            session.Version++;
            await InsertAuditEventAsync(connection, transaction, session, "session.ended", actorUserId, reason);
            await InsertRealtimeEventAsync(
                connection,
                transaction,
                DeliveryRealtimeEvents.MotoboyStatusChanged,
                "presence.offline",
                session,
                new { status = "offline", reason });
            return session;
        }

        private async Task<int> ExpireDueSessionsInternalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int limit)
        {
            var serverNow = await GetServerNowAsync(connection, transaction);
            var invalidLinks = (await connection.QueryAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.session_id IN (
     SELECT s2.session_id
       FROM motoboy_active_sessions s2
      WHERE s2.ended_at_utc IS NULL
        AND s2.revoked_at IS NULL
        AND NOT EXISTS (
            SELECT 1
              FROM motoboy_estabelecimento me
              JOIN estabelecimentos e ON e.id = me.estabelecimento_id
              JOIN motoboy m ON m.id = me.motoboy_id
             WHERE me.motoboy_id = s2.motoboy_id
               AND me.estabelecimento_id = s2.id_estabelecimento
               AND me.ativo = TRUE
               AND COALESCE(e.ativo, TRUE) = TRUE
               AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
               AND (
                   (s2.origin = 'simulator' AND me.simulator_enabled = TRUE AND m.is_simulated = TRUE)
                   OR (
                       s2.origin = 'mobile'
                   )
               )
        )
      ORDER BY s2.started_at_utc
      FOR UPDATE SKIP LOCKED
      LIMIT @Limit
 );",
                new { Limit = Math.Clamp(limit, 1, 1000) },
                transaction)).ToList();
            foreach (var session in invalidLinks)
            {
                await EndLockedSessionAsync(connection, transaction, session, "link_revoked", null);
            }

            var remaining = Math.Max(0, Math.Clamp(limit, 1, 1000) - invalidLinks.Count);
            if (remaining == 0)
            {
                return invalidLinks.Count;
            }

            var sessions = (await connection.QueryAsync<OperationalSessionRecord>(
                SessionSelect + @"
 WHERE s.session_id IN (
     SELECT session_id
       FROM motoboy_active_sessions
      WHERE ended_at_utc IS NULL
        AND revoked_at IS NULL
        AND expires_at_utc <= @ServerNow
      ORDER BY expires_at_utc
      FOR UPDATE SKIP LOCKED
      LIMIT @Limit
 );",
                new { ServerNow = serverNow, Limit = remaining },
                transaction)).ToList();
            foreach (var session in sessions)
            {
                await EndLockedSessionAsync(connection, transaction, session, "heartbeat_timeout", null);
            }

            return invalidLinks.Count + sessions.Count;
        }

        private static void EnsureAttemptCanBeReturned(OperationalSessionRecord session)
        {
            if (session.EndedAtUtc.HasValue || session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new DeliveryDomainException(409, "ATTEMPT_CLOSED", "O attemptId pertence a uma sessao ja encerrada.");
            }
        }

        private static void EnsureAttemptScope(OperationalSessionRecord session, Guid estabelecimentoId)
        {
            if (session.EstabelecimentoId != estabelecimentoId)
            {
                throw new DeliveryDomainException(
                    409,
                    "ATTEMPT_SCOPE_MISMATCH",
                    "O attemptId ja foi usado em outro estabelecimento.");
            }
        }

        private static Task AcquireAttemptLockAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid attemptId)
        {
            return connection.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended(@AttemptId::text, 0));",
                new { AttemptId = attemptId },
                transaction);
        }

        private static DeliveryDomainException SessionConflict(OperationalSessionRecord session) =>
            new(
                409,
                "SESSION_CONFLICT",
                "O motoboy ja possui uma sessao operacional ativa.",
                new OperationalSessionConflictDto
                {
                    SessionId = session.SessionId,
                    EstablishmentId = session.EstabelecimentoId,
                    Origin = session.Origin,
                    PresenceExpiresAtUtc = session.ExpiresAtUtc
                });

        private static Task<DateTimeOffset> GetServerNowAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction) =>
            connection.ExecuteScalarAsync<DateTimeOffset>("SELECT NOW();", transaction: transaction);

        private static Task InsertAuditEventAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            OperationalSessionRecord session,
            string eventType,
            int? actorUserId,
            string? reason)
        {
            return connection.ExecuteAsync(@"
INSERT INTO motoboy_operational_session_events (
    event_id, session_id, motoboy_id, estabelecimento_id, event_type,
    actor_user_id, reason, details, occurred_at_utc)
VALUES (
    @EventId, @SessionId, @MotoboyId, @EstabelecimentoId, @EventType,
    @ActorUserId, @Reason, @Details::jsonb, NOW());",
                new
                {
                    EventId = Guid.NewGuid(),
                    session.SessionId,
                    session.MotoboyId,
                    session.EstabelecimentoId,
                    EventType = eventType,
                    ActorUserId = actorUserId,
                    Reason = reason,
                    Details = JsonSerializer.Serialize(new
                    {
                        sessionEpoch = session.SessionEpoch,
                        origin = session.Origin,
                        version = session.Version
                    })
                },
                transaction);
        }

        private static Task InsertRealtimeEventAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string eventName,
            string eventType,
            OperationalSessionRecord session,
            object data, CancellationToken cancellationToken = default)
        {
            var eventId = Guid.NewGuid();
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId,
                eventType,
                estabelecimentoId = session.EstabelecimentoId,
                motoboyId = session.MotoboyId,
                sessionId = session.SessionId,
                sessionEpoch = session.SessionEpoch,
                version = session.Version,
                occurredAtUtc,
                data
            });
            return connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO delivery_realtime_outbox (
    event_id, event_name, target_group, estabelecimento_id, motoboy_id,
    session_id, session_epoch, aggregate_version, payload, occurred_at_utc)
VALUES (
    @EventId, @EventName, @TargetGroup, @EstabelecimentoId, @MotoboyId,
    @SessionId, @SessionEpoch, @AggregateVersion, @Payload::jsonb, @OccurredAtUtc);",
                new
                {
                    EventId = eventId,
                    EventName = eventName,
                    TargetGroup = DeliveryRealtimeEvents.EstablishmentGroup(session.EstabelecimentoId),
                    session.EstabelecimentoId,
                    session.MotoboyId,
                    session.SessionId,
                    session.SessionEpoch,
                    AggregateVersion = session.Version,
                    Payload = payload,
                    OccurredAtUtc = occurredAtUtc
                },
                transaction, commandTimeout: 10, cancellationToken: cancellationToken));
        }

        private sealed class SnapshotRow
        {
            public int MotoboyId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string? Avatar { get; set; }
            public Guid SessionId { get; set; }
            public long SessionEpoch { get; set; }
            public long Version { get; set; }
            public string Origin { get; set; } = string.Empty;
            public DateTimeOffset PresenceExpiresAtUtc { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? AccuracyMeters { get; set; }
            public double? SpeedMps { get; set; }
            public double? HeadingDegrees { get; set; }
            public string? TrackingMode { get; set; }
            public string? Quality { get; set; }
            public long? Sequence { get; set; }
            public DateTimeOffset? CapturedAtUtc { get; set; }
            public DateTimeOffset? ReceivedAtUtc { get; set; }
            public bool HasActiveRouteStop { get; set; }
        }
    }
}
