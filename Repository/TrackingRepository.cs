using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class TrackingRepository : ITrackingRepository
    {
        private readonly string _connectionString;

        public TrackingRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection nao configurada.");
        }

        // Schema de delivery e aplicado exclusivamente pelas migrations versionadas.
        public Task EnsureSchemaAsync() => Task.CompletedTask;

        public async Task<MotoboyTrackingIdentity?> ResolveMotoboyForUserAsync(int userId, Guid estabelecimentoId)
        {
            await EnsureSchemaAsync();

            const string resolveSql = @"
SELECT
    m.id AS MotoboyId,
    m.id_usuario AS UsuarioId,
    COALESCE(m.id_estabelecimento, @EstabelecimentoId) AS EstabelecimentoId,
    COALESCE(m.nome, '') AS Nome,
    m.avatar AS Avatar,
    COALESCE(m.status, 2) AS Status
FROM motoboy m
WHERE
    (m.id_usuario = @UserId OR (m.id_usuario IS NULL AND m.id = @UserId))
    AND (m.id_estabelecimento IS NULL OR m.id_estabelecimento = @EstabelecimentoId)
ORDER BY
    CASE WHEN m.id_usuario = @UserId THEN 0 ELSE 1 END,
    m.id
LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var identity = await connection.QueryFirstOrDefaultAsync<MotoboyTrackingIdentity>(
                resolveSql,
                new { UserId = userId, EstabelecimentoId = estabelecimentoId });

            if (identity != null)
            {
                return identity;
            }

            const string createSql = @"
WITH user_access AS (
    SELECT u.id AS usuario_id, COALESCE(NULLIF(TRIM(u.nome), ''), u.email, 'Motoboy') AS nome
      FROM usuario u
      JOIN usuario_estabelecimentos ue ON ue.id_usuario = u.id
     WHERE u.id = @UserId
       AND u.deleted_at IS NULL
       AND ue.id_estabelecimento = @EstabelecimentoId
       AND LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
       AND COALESCE(ue.ativo, TRUE) = TRUE
       AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
     LIMIT 1
),
updated AS (
    UPDATE motoboy m
       SET nome = user_access.nome,
           id_usuario = @UserId,
           id_estabelecimento = @EstabelecimentoId
      FROM user_access
     WHERE (m.id_usuario = @UserId AND (m.id_estabelecimento = @EstabelecimentoId OR m.id_estabelecimento IS NULL))
        OR (m.id_usuario IS NULL AND LOWER(COALESCE(m.nome, '')) = LOWER(user_access.nome) AND (m.id_estabelecimento = @EstabelecimentoId OR m.id_estabelecimento IS NULL))
 RETURNING m.id AS MotoboyId,
           @UserId AS UsuarioId,
           @EstabelecimentoId AS EstabelecimentoId,
           COALESCE(m.nome, '') AS Nome,
           m.avatar AS Avatar,
           COALESCE(m.status, 2) AS Status
),
inserted AS (
    INSERT INTO motoboy (nome, status, id_usuario, id_estabelecimento)
    SELECT nome, 2, @UserId, @EstabelecimentoId
      FROM user_access
     WHERE NOT EXISTS (SELECT 1 FROM updated)
 RETURNING id AS MotoboyId,
           id_usuario AS UsuarioId,
           id_estabelecimento AS EstabelecimentoId,
           nome AS Nome,
           avatar AS Avatar,
           status AS Status
)
SELECT * FROM updated
UNION ALL
SELECT * FROM inserted
LIMIT 1;";

            return await connection.QueryFirstOrDefaultAsync<MotoboyTrackingIdentity>(
                createSql,
                new { UserId = userId, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<MotoboyTrackingIdentity?> ResolveMotoboyByIdAsync(int motoboyId, Guid estabelecimentoId)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT
    m.id AS MotoboyId,
    m.id_usuario AS UsuarioId,
    COALESCE(m.id_estabelecimento, @EstabelecimentoId) AS EstabelecimentoId,
    COALESCE(m.nome, '') AS Nome,
    m.avatar AS Avatar,
    COALESCE(m.status, 2) AS Status
FROM motoboy m
WHERE m.id = @MotoboyId
  AND (m.id_estabelecimento = @EstabelecimentoId OR m.id_estabelecimento IS NULL)
LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<MotoboyTrackingIdentity>(
                sql,
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<MotoboyTrackingIdentity> CreateSimulatorMotoboyAsync(
            Guid estabelecimentoId,
            string nome,
            string? telefone)
        {
            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO motoboy (nome, telefone, status, id_estabelecimento)
VALUES (@Nome, @Telefone, 2, @EstabelecimentoId)
RETURNING
    id AS MotoboyId,
    id_usuario AS UsuarioId,
    id_estabelecimento AS EstabelecimentoId,
    COALESCE(nome, '') AS Nome,
    avatar AS Avatar,
    COALESCE(status, 2) AS Status;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QuerySingleAsync<MotoboyTrackingIdentity>(sql, new
            {
                Nome = string.IsNullOrWhiteSpace(nome) ? "Motoboy Simulado" : nome.Trim(),
                Telefone = string.IsNullOrWhiteSpace(telefone) ? null : telefone.Trim(),
                EstabelecimentoId = estabelecimentoId
            });
        }

        public async Task<Guid> StartMotoboySessionAsync(
            MotoboyTrackingIdentity identity,
            Guid estabelecimentoId,
            string deviceType)
        {
            await Task.CompletedTask;
            throw new NotSupportedException(
                "Inicio de sessao legado desabilitado. Use o contrato operacional /api/v2.");
        }

        public async Task SetMotoboyStatusAsync(int motoboyId, int userId, Guid estabelecimentoId, int status)
        {
            await EnsureSchemaAsync();

            const string sql = @"
UPDATE motoboy
   SET status = @Status,
       id_usuario = COALESCE(id_usuario, @UserId),
       id_estabelecimento = COALESCE(id_estabelecimento, @EstabelecimentoId)
 WHERE id = @MotoboyId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                Status = status,
                UserId = userId,
                EstabelecimentoId = estabelecimentoId,
                MotoboyId = motoboyId
            });
        }

        public async Task SetMotoboyStatusAsync(int motoboyId, Guid estabelecimentoId, int status)
        {
            await EnsureSchemaAsync();

            const string sql = @"
UPDATE motoboy
   SET status = @Status,
       id_estabelecimento = COALESCE(id_estabelecimento, @EstabelecimentoId)
 WHERE id = @MotoboyId
   AND (id_estabelecimento = @EstabelecimentoId OR id_estabelecimento IS NULL);";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                Status = status,
                EstabelecimentoId = estabelecimentoId,
                MotoboyId = motoboyId
            });
        }

        public async Task<MotoboyLocationState?> GetLocationStateAsync(int motoboyId)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT
    motoboy_id AS MotoboyId,
    id_estabelecimento AS EstabelecimentoId,
    latitude AS Latitude,
    longitude AS Longitude,
    accuracy_meters AS AccuracyMeters,
    speed_mps AS SpeedMps,
    heading_degrees AS HeadingDegrees,
    tracking_mode AS TrackingMode,
    quality AS Quality,
    last_sequence AS LastSequence,
    client_timestamp_utc AS ClientTimestampUtc,
    server_received_at_utc AS ServerReceivedAtUtc,
    updated_at AS UpdatedAt
FROM motoboy_location_state
WHERE motoboy_id = @MotoboyId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<MotoboyLocationState>(sql, new { MotoboyId = motoboyId });
        }

        public async Task UpsertLocationStateAsync(MotoboyTrackingIdentity identity, MotoboyLocationState state)
        {
            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO motoboy_location_state (
    motoboy_id, id_estabelecimento, latitude, longitude, accuracy_meters, speed_mps, heading_degrees,
    tracking_mode, quality, last_sequence, client_timestamp_utc, server_received_at_utc, updated_at
) VALUES (
    @MotoboyId, @EstabelecimentoId, @Latitude, @Longitude, @AccuracyMeters, @SpeedMps, @HeadingDegrees,
    @TrackingMode, @Quality, @LastSequence, @ClientTimestampUtc, @ServerReceivedAtUtc, NOW()
)
ON CONFLICT (motoboy_id) DO UPDATE SET
    id_estabelecimento = EXCLUDED.id_estabelecimento,
    latitude = EXCLUDED.latitude,
    longitude = EXCLUDED.longitude,
    accuracy_meters = EXCLUDED.accuracy_meters,
    speed_mps = EXCLUDED.speed_mps,
    heading_degrees = EXCLUDED.heading_degrees,
    tracking_mode = EXCLUDED.tracking_mode,
    quality = EXCLUDED.quality,
    last_sequence = EXCLUDED.last_sequence,
    client_timestamp_utc = EXCLUDED.client_timestamp_utc,
    server_received_at_utc = EXCLUDED.server_received_at_utc,
    updated_at = NOW();

UPDATE motoboy
   SET latitude = @LatitudeText,
       longitude = @LongitudeText,
       id_estabelecimento = COALESCE(id_estabelecimento, @EstabelecimentoId),
       status = CASE WHEN @TrackingMode = 'active_route' THEN 5 ELSE status END
 WHERE id = @MotoboyId;";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                state.MotoboyId,
                state.EstabelecimentoId,
                state.Latitude,
                state.Longitude,
                state.AccuracyMeters,
                state.SpeedMps,
                state.HeadingDegrees,
                state.TrackingMode,
                state.Quality,
                state.LastSequence,
                state.ClientTimestampUtc,
                state.ServerReceivedAtUtc,
                LatitudeText = state.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LongitudeText = state.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        public async Task<MotoboyLocationHistoryPoint?> GetLastHistoryPointAsync(int motoboyId, DateOnly localDate)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT
    id AS Id,
    motoboy_id AS MotoboyId,
    id_estabelecimento AS EstabelecimentoId,
    latitude AS Latitude,
    longitude AS Longitude,
    accuracy_meters AS AccuracyMeters,
    speed_mps AS SpeedMps,
    heading_degrees AS HeadingDegrees,
    tracking_mode AS TrackingMode,
    quality AS Quality,
    client_timestamp_utc AS ClientTimestampUtc,
    server_received_at_utc AS ServerReceivedAtUtc,
    local_date AS LocalDate,
    sequence AS Sequence
FROM motoboy_location_history_daily
WHERE motoboy_id = @MotoboyId
  AND local_date = @LocalDate
ORDER BY client_timestamp_utc DESC, id DESC
LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<MotoboyLocationHistoryPoint>(sql, new
            {
                MotoboyId = motoboyId,
                LocalDate = localDate
            });
        }

        public async Task InsertHistoryPointAsync(MotoboyLocationHistoryPoint point)
        {
            await EnsureSchemaAsync();

            const string sql = @"
INSERT INTO motoboy_location_history_daily (
    motoboy_id, id_estabelecimento, latitude, longitude, accuracy_meters, speed_mps, heading_degrees,
    tracking_mode, quality, client_timestamp_utc, server_received_at_utc, local_date, sequence
) VALUES (
    @MotoboyId, @EstabelecimentoId, @Latitude, @Longitude, @AccuracyMeters, @SpeedMps, @HeadingDegrees,
    @TrackingMode, @Quality, @ClientTimestampUtc, @ServerReceivedAtUtc, @LocalDate, @Sequence
);";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, point);
        }

        public async Task CleanupOldHistoryAsync(DateOnly keepFromLocalDate)
        {
            await EnsureSchemaAsync();

            const string sql = "DELETE FROM motoboy_location_history_daily WHERE local_date < @KeepFromLocalDate;";
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { KeepFromLocalDate = keepFromLocalDate });
        }

        public async Task<string> GetEstablishmentTimezoneAsync(Guid estabelecimentoId)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT COALESCE(timezone_iana, 'America/Sao_Paulo')
FROM estabelecimentos
WHERE id = @EstabelecimentoId
LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.ExecuteScalarAsync<string>(sql, new { EstabelecimentoId = estabelecimentoId })
                    ?? "America/Sao_Paulo";
            }
            catch
            {
                return "America/Sao_Paulo";
            }
        }

        public async Task<DeliveryMapStateDto> GetMapStateAsync(Guid estabelecimentoId)
        {
            await EnsureSchemaAsync();

            const string motoboysSql = @"
SELECT
    m.id AS Id,
    COALESCE(m.nome, '') AS Nome,
    m.avatar AS Avatar,
    CASE COALESCE(m.status, 2)
        WHEN 1 THEN 'online'
        WHEN 5 THEN 'delivering'
        ELSE 'offline'
    END AS Status,
    COALESCE(
        s.latitude,
        CASE WHEN m.latitude ~ '^-?[0-9]+(\.[0-9]+)?$' THEN m.latitude::DOUBLE PRECISION ELSE NULL END
    ) AS Latitude,
    COALESCE(
        s.longitude,
        CASE WHEN m.longitude ~ '^-?[0-9]+(\.[0-9]+)?$' THEN m.longitude::DOUBLE PRECISION ELSE NULL END
    ) AS Longitude,
    s.accuracy_meters AS AccuracyMeters,
    s.speed_mps AS SpeedMps,
    s.heading_degrees AS HeadingDegrees,
    COALESCE(s.tracking_mode, 'online_idle') AS TrackingMode,
    COALESCE(s.quality, 'unknown') AS Quality,
    s.client_timestamp_utc AS ClientTimestampUtc,
    s.server_received_at_utc AS ServerReceivedAtUtc
FROM motoboy m
LEFT JOIN motoboy_location_state s ON s.motoboy_id = m.id
WHERE
    m.id_estabelecimento = @EstabelecimentoId
    OR s.id_estabelecimento = @EstabelecimentoId
ORDER BY m.nome;";

            const string numeric = @"'^-?[0-9]+(\.[0-9]+)?$'";

            // Colunas legadas de pedido podem ser text/numeric/time/timestamp: tudo e lido
            // como texto e convertido com seguranca em C#. Um valor mal formatado vira
            // null naquele campo em vez de derrubar o painel inteiro com 500.
            var pedidosSql = $@"
SELECT
    p.id AS Id,
    p.nome_cliente::text AS NomeCliente,
    p.id_ifood::text AS IdIfood,
    p.telefone_cliente::text AS TelefoneCliente,
    p.data_pedido::text AS DataPedidoRaw,
    p.endereco_entrega::text AS EnderecoEntrega,
    p.items::text AS Items,
    CASE WHEN p.value::text ~ {numeric} THEN p.value::text::NUMERIC END AS Value,
    p.region::text AS Region,
    CASE COALESCE(p.status_pedido, 1)
        WHEN 1 THEN 'pendente'
        WHEN 2 THEN 'em_rota'
        WHEN 3 THEN 'concluido'
        WHEN 4 THEN 'cancelado'
        WHEN 5 THEN 'atribuido'
        ELSE 'pendente'
    END AS StatusPedido,
    p.motoboy_responsavel AS AssignedDriver,
    CASE WHEN p.latitude::text ~ {numeric} THEN p.latitude::text::DOUBLE PRECISION END AS Latitude,
    CASE WHEN p.longitude::text ~ {numeric} THEN p.longitude::text::DOUBLE PRECISION END AS Longitude,
    p.horario_pedido::text AS HorarioPedidoRaw,
    p.previsao_entrega::text AS PrevisaoEntregaRaw,
    p.horario_saida::text AS HorarioSaidaRaw,
    p.horario_entrega::text AS HorarioEntregaRaw,
    p.tipo_pagamento::text AS TipoPagamento,
    p.status_pagamento::text AS StatusPagamento,
    CASE WHEN p.troco::text ~ {numeric} THEN p.troco::text::NUMERIC END AS Troco,
    CASE WHEN p.distancia_km::text ~ {numeric} THEN p.distancia_km::text::NUMERIC END AS DistanciaKm,
    p.observacoes::text AS Observacoes,
    p.codigo_entrega::text AS CodigoEntrega,
    p.entrega_rua::text AS EntregaRua,
    p.entrega_numero::text AS EntregaNumero,
    p.entrega_bairro::text AS EntregaBairro,
    p.entrega_cidade::text AS EntregaCidade,
    p.entrega_estado::text AS EntregaEstado,
    p.entrega_cep::text AS EntregaCep,
    rs.position AS RoutePosition,
    rs.stop_status AS RouteStopStatus,
    rs.picked_up_at_utc AS PickedUpAtUtc,
    rs.arrived_at_utc AS ArrivedAtUtc,
    lf.reason AS LastFailureReason,
    lf.kind AS LastFailureKind,
    lf.at_utc AS LastFailureAtUtc,
    lf.motoboy_id AS LastFailureMotoboyId,
    lf.motoboy_nome AS LastFailureMotoboyNome,
    COALESCE(tries.total, 0) AS AttemptCount,
    dn.at_utc AS CompletedAtUtc,
    dn.motoboy_id AS CompletedByMotoboyId,
    dn.motoboy_nome AS CompletedByMotoboyNome,
    cn.at_utc AS CanceledAtUtc,
    COALESCE(pt.pending, FALSE) AS HasPendingTransfer,
    pt.to_nome AS PendingTransferToNome
FROM pedido p
LEFT JOIN delivery_route_stops rs
       ON rs.pedido_id = p.id
      AND rs.estabelecimento_id = p.id_estabelecimento
      AND rs.stop_status IN ('assigned', 'en_route')
LEFT JOIN LATERAL (
    SELECT CASE WHEN f.stop_status = 'failed' THEN f.failure_reason ELSE f.refusal_reason END AS reason,
           f.stop_status AS kind,
           COALESCE(f.failed_at_utc, f.refused_at_utc) AS at_utc,
           f.motoboy_id AS motoboy_id,
           fm.nome::text AS motoboy_nome
      FROM delivery_route_stops f
      LEFT JOIN motoboy fm ON fm.id = f.motoboy_id
     WHERE f.pedido_id = p.id
       AND f.stop_status IN ('failed', 'refused')
     ORDER BY COALESCE(f.failed_at_utc, f.refused_at_utc) DESC NULLS LAST
     LIMIT 1
) lf ON COALESCE(p.status_pedido, 1) = 1
LEFT JOIN LATERAL (
    SELECT COUNT(*)::int AS total
      FROM delivery_route_stops a
     WHERE a.pedido_id = p.id
       AND a.stop_status IN ('failed', 'refused')
) tries ON TRUE
LEFT JOIN LATERAL (
    SELECT d.completed_at_utc AS at_utc,
           d.motoboy_id AS motoboy_id,
           dm.nome::text AS motoboy_nome
      FROM delivery_route_stops d
      LEFT JOIN motoboy dm ON dm.id = d.motoboy_id
     WHERE d.pedido_id = p.id
       AND d.stop_status = 'completed'
     ORDER BY d.completed_at_utc DESC NULLS LAST
     LIMIT 1
) dn ON COALESCE(p.status_pedido, 1) = 3
LEFT JOIN LATERAL (
    SELECT c.canceled_at_utc AS at_utc
      FROM delivery_route_stops c
     WHERE c.pedido_id = p.id
       AND c.stop_status = 'canceled'
     ORDER BY c.canceled_at_utc DESC NULLS LAST
     LIMIT 1
) cn ON COALESCE(p.status_pedido, 1) = 4
LEFT JOIN LATERAL (
    SELECT TRUE AS pending,
           tm.nome::text AS to_nome
      FROM delivery_transfer_requests t
      LEFT JOIN motoboy tm ON tm.id = t.to_motoboy_id
     WHERE t.pedido_id = p.id
       AND t.estabelecimento_id = p.id_estabelecimento
       AND t.status = 'pending_approval'
     ORDER BY t.requested_at_utc DESC
     LIMIT 1
) pt ON COALESCE(p.status_pedido, 1) IN (2, 5)
 WHERE p.id_estabelecimento = @EstabelecimentoId
   -- Ativos (pendente/em_rota/atribuido) mais os desfechos (concluido/cancelado) a partir do
   -- inicio da janela de pedidos: o painel mostra 'Entregue' em vez de sumir com o pedido.
   -- O recorte fino pela janela configurada do estabelecimento e feito depois, em C#, porque
   -- as colunas de horario do pedido sao legadas e de tipos misturados.
   AND (
        COALESCE(p.status_pedido, 1) IN (1, 2, 5)
        OR (COALESCE(p.status_pedido, 1) = 3 AND dn.at_utc >= @WindowFromUtc)
        OR (COALESCE(p.status_pedido, 1) = 4 AND cn.at_utc >= @WindowFromUtc)
   )
ORDER BY p.data_pedido DESC NULLS LAST, p.id DESC;";

            // Mesmo tratamento defensivo das coordenadas de pedido/motoboy: so converte o
            // que for numero valido, para nao derrubar o mapa por um cadastro mal preenchido.
            const string estabelecimentoSql = @"
SELECT
    CASE WHEN e.latitude::text ~ '^-?[0-9]+(\.[0-9]+)?$' THEN e.latitude::text::DOUBLE PRECISION ELSE NULL END AS Latitude,
    CASE WHEN e.longitude::text ~ '^-?[0-9]+(\.[0-9]+)?$' THEN e.longitude::text::DOUBLE PRECISION ELSE NULL END AS Longitude,
    NULLIF(BTRIM(e.cidade::text), '') AS Cidade,
    NULLIF(BTRIM(e.uf::text), '') AS Uf
FROM estabelecimentos e
WHERE e.id = @EstabelecimentoId;";

            // Dia operacional no fuso do Brasil (o servidor roda em UTC).
            const string metricsSql = @"
SELECT
    COUNT(*) FILTER (WHERE rs.stop_status = 'completed' AND rs.completed_at_utc >= @FromUtc AND rs.completed_at_utc < @ToUtc)::int AS DeliveredToday,
    COUNT(*) FILTER (WHERE rs.stop_status = 'failed' AND rs.failed_at_utc >= @FromUtc AND rs.failed_at_utc < @ToUtc)::int AS FailedToday,
    AVG(EXTRACT(EPOCH FROM (rs.completed_at_utc - COALESCE(rs.started_at_utc, rs.assigned_at_utc))) / 60.0)
        FILTER (WHERE rs.stop_status = 'completed' AND rs.completed_at_utc >= @FromUtc AND rs.completed_at_utc < @ToUtc)::double precision AS AvgDeliveryMinutesToday,
    (SELECT COUNT(*)::int FROM delivery_transfer_requests t
      WHERE t.estabelecimento_id = @EstabelecimentoId AND t.status = 'pending_approval') AS PendingTransferApprovals
FROM delivery_route_stops rs
WHERE rs.estabelecimento_id = @EstabelecimentoId
  AND COALESCE(rs.completed_at_utc, rs.failed_at_utc) >= @FromUtc;";

            const string motoboyStatsSql = @"
SELECT rs.motoboy_id AS MotoboyId, COUNT(*)::int AS DeliveredToday
  FROM delivery_route_stops rs
 WHERE rs.estabelecimento_id = @EstabelecimentoId
   AND rs.stop_status = 'completed'
   AND rs.completed_at_utc >= @FromUtc
   AND rs.completed_at_utc < @ToUtc
 GROUP BY rs.motoboy_id;";

            var (fromUtc, toUtc) = OperationalDayWindow.ToUtcRange(OperationalDayWindow.Today(DateTimeOffset.UtcNow));
            var dayParams = new { EstabelecimentoId = estabelecimentoId, FromUtc = fromUtc, ToUtc = toUtc };

            await using var connection = new NpgsqlConnection(_connectionString);
            var motoboys = (await connection.QueryAsync<MotoboyMapDto>(motoboysSql, new { EstabelecimentoId = estabelecimentoId })).ToList();
            var orderWindow = await OrderWindowStore.ReadAsync(connection, estabelecimentoId);
            var windowRange = OrderWindowRules.Resolve(orderWindow, DateTimeOffset.UtcNow);
            // Sem inicio (janela so com fim), olha 7 dias para tras: mais que isso nao e operacao.
            var windowFromUtc = windowRange.FromUtc ?? fromUtc.AddDays(-7);
            var pedidos = (await connection.QueryAsync<OrderMapRow>(
                    pedidosSql, new { EstabelecimentoId = estabelecimentoId, WindowFromUtc = windowFromUtc }))
                .Select(ToOrderMapDto)
                .Where(pedido => IsInOrderWindow(pedido, orderWindow, windowRange))
                .ToList();
            var estabelecimento = await connection.QuerySingleOrDefaultAsync<EstabelecimentoLocationRow>(
                estabelecimentoSql, new { EstabelecimentoId = estabelecimentoId });
            var metrics = await connection.QuerySingleOrDefaultAsync<DeliveryDayMetricsDto>(metricsSql, dayParams)
                ?? new DeliveryDayMetricsDto();
            var motoboyStats = (await connection.QueryAsync<MotoboyDayStatsDto>(motoboyStatsSql, dayParams)).ToList();

            var pedidosPorMotoboy = pedidos
                .Where(p => p.AssignedDriver.HasValue && (p.StatusPedido == "em_rota" || p.StatusPedido == "atribuido"))
                .GroupBy(p => p.AssignedDriver!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var motoboy in motoboys)
            {
                if (!pedidosPorMotoboy.TryGetValue(motoboy.Id, out var pedidosDoMotoboy))
                {
                    continue;
                }

                motoboy.Pedidos = pedidosDoMotoboy
                    .Where(p => p.Coordinates != null)
                    .OrderBy(p => p.RoutePosition ?? int.MaxValue)
                    .Select(p => new DeliveryMapItemDto
                    {
                        Id = p.Id,
                        Status = p.StatusPedido == "em_rota" ? "em_rota" : "proxima",
                        Address = p.EnderecoEntrega ?? string.Empty,
                        Items = p.Items ?? string.Empty,
                        Value = p.Value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                        DepartureTime = p.HorarioSaida?.ToString("HH:mm"),
                        Eta = p.PrevisaoEntrega?.ToString("HH:mm"),
                        EtaMinutes = 0,
                        Coordinates = p.Coordinates ?? Array.Empty<double>()
                    })
                    .ToList();
            }

            return new DeliveryMapStateDto
            {
                ServerTimeUtc = DateTimeOffset.UtcNow,
                EstabelecimentoLatitude = estabelecimento?.Latitude,
                EstabelecimentoLongitude = estabelecimento?.Longitude,
                EstabelecimentoCidade = estabelecimento?.Cidade,
                EstabelecimentoUf = estabelecimento?.Uf,
                Metrics = metrics,
                MotoboyStats = motoboyStats,
                Motoboys = motoboys,
                Pedidos = pedidos
            };
        }

        /// <summary>
        /// Pedido em aberto aparece sempre (se a janela pedir); os demais, quando foram feitos
        /// dentro da janela OU quando o desfecho (entrega/cancelamento) aconteceu nela. Sem
        /// horario confiavel o pedido nao some: melhor sobrar um do que esconder um.
        /// </summary>
        internal static bool IsInOrderWindow(OrderMapDto pedido, DTOs.Delivery.OrderWindowDto window, OrderWindowRange range)
        {
            var open = pedido.StatusPedido is "pendente" or "atribuido" or "em_rota";
            if (open && window.AlwaysShowOpenOrders) return true;

            var placed = OrderWindowRules.PlacedAtUtc(pedido.HorarioPedido ?? pedido.DataPedido);
            if (placed is null) return true;
            if (range.Contains(placed.Value)) return true;

            var finishedAt = pedido.CompletedAtUtc ?? pedido.CanceledAtUtc;
            return !open && finishedAt is not null && range.Contains(finishedAt.Value);
        }

        private sealed class OrderMapRow
        {
            public int Id { get; set; }
            public string? NomeCliente { get; set; }
            public string? IdIfood { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? DataPedidoRaw { get; set; }
            public string? EnderecoEntrega { get; set; }
            public string? Items { get; set; }
            public decimal? Value { get; set; }
            public string? Region { get; set; }
            public string StatusPedido { get; set; } = "pendente";
            public int? AssignedDriver { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? HorarioPedidoRaw { get; set; }
            public string? PrevisaoEntregaRaw { get; set; }
            public string? HorarioSaidaRaw { get; set; }
            public string? HorarioEntregaRaw { get; set; }
            public string? TipoPagamento { get; set; }
            public string? StatusPagamento { get; set; }
            public decimal? Troco { get; set; }
            public decimal? DistanciaKm { get; set; }
            public string? Observacoes { get; set; }
            public string? CodigoEntrega { get; set; }
            public string? EntregaRua { get; set; }
            public string? EntregaNumero { get; set; }
            public string? EntregaBairro { get; set; }
            public string? EntregaCidade { get; set; }
            public string? EntregaEstado { get; set; }
            public string? EntregaCep { get; set; }
            public int? RoutePosition { get; set; }
            public string? RouteStopStatus { get; set; }
            public DateTimeOffset? PickedUpAtUtc { get; set; }
            public DateTimeOffset? ArrivedAtUtc { get; set; }
            public string? LastFailureReason { get; set; }
            public string? LastFailureKind { get; set; }
            public DateTimeOffset? LastFailureAtUtc { get; set; }
            public int? LastFailureMotoboyId { get; set; }
            public string? LastFailureMotoboyNome { get; set; }
            public int AttemptCount { get; set; }
            public DateTimeOffset? CompletedAtUtc { get; set; }
            public int? CompletedByMotoboyId { get; set; }
            public string? CompletedByMotoboyNome { get; set; }
            public DateTimeOffset? CanceledAtUtc { get; set; }
            public bool? HasPendingTransfer { get; set; }
            public string? PendingTransferToNome { get; set; }
        }

        private static OrderMapDto ToOrderMapDto(OrderMapRow row)
        {
            var dataPedido = DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw);
            var horarioPedido = DeliveryRules.ParseStoredDateTime(row.HorarioPedidoRaw, dataPedido);
            var previsao = DeliveryRules.ParseStoredDateTime(row.PrevisaoEntregaRaw, dataPedido);
            // Previsao so com horario, ancorada na data do pedido, pode cair "antes" do
            // pedido quando passa da meia-noite (pedido 23:50, previsao 00:30).
            if (previsao.HasValue && horarioPedido.HasValue && previsao.Value < horarioPedido.Value
                && previsao.Value.Kind == DateTimeKind.Unspecified && horarioPedido.Value.Kind == DateTimeKind.Unspecified)
            {
                previsao = previsao.Value.AddDays(1);
            }

            return new OrderMapDto
            {
                Id = row.Id,
                NomeCliente = row.NomeCliente,
                IdIfood = row.IdIfood,
                TelefoneCliente = row.TelefoneCliente,
                DataPedido = dataPedido,
                EnderecoEntrega = row.EnderecoEntrega,
                Items = row.Items,
                Value = row.Value,
                Region = row.Region,
                StatusPedido = row.StatusPedido,
                AssignedDriver = row.AssignedDriver,
                Latitude = row.Latitude,
                Longitude = row.Longitude,
                HorarioPedido = horarioPedido,
                PrevisaoEntrega = previsao,
                HorarioSaida = DeliveryRules.ParseStoredDateTime(row.HorarioSaidaRaw, dataPedido),
                HorarioEntrega = DeliveryRules.ParseStoredDateTime(row.HorarioEntregaRaw, dataPedido),
                TipoPagamento = row.TipoPagamento,
                StatusPagamento = row.StatusPagamento,
                Troco = row.Troco,
                DistanciaKm = row.DistanciaKm,
                Observacoes = row.Observacoes,
                // O codigo so importa enquanto o pedido esta em aberto: finalizado nao o expoe mais.
                CodigoEntrega = row.StatusPedido is "pendente" or "atribuido" or "em_rota"
                    ? (string.IsNullOrWhiteSpace(row.CodigoEntrega) ? null : row.CodigoEntrega.Trim())
                    : null,
                EntregaRua = row.EntregaRua,
                EntregaNumero = row.EntregaNumero,
                EntregaBairro = row.EntregaBairro,
                EntregaCidade = row.EntregaCidade,
                EntregaEstado = row.EntregaEstado,
                EntregaCep = row.EntregaCep,
                RoutePosition = row.RoutePosition,
                RouteStopStatus = row.RouteStopStatus,
                PickedUpAtUtc = row.PickedUpAtUtc,
                ArrivedAtUtc = row.ArrivedAtUtc,
                LastFailureReason = row.LastFailureReason,
                LastFailureKind = row.LastFailureKind,
                LastFailureAtUtc = row.LastFailureAtUtc,
                LastFailureMotoboyId = row.LastFailureMotoboyId,
                LastFailureMotoboyNome = row.LastFailureMotoboyNome,
                AttemptCount = row.AttemptCount,
                CompletedAtUtc = row.CompletedAtUtc,
                CompletedByMotoboyId = row.CompletedByMotoboyId,
                CompletedByMotoboyNome = row.CompletedByMotoboyNome,
                CanceledAtUtc = row.CanceledAtUtc,
                HasPendingTransfer = row.HasPendingTransfer ?? false,
                PendingTransferToNome = row.PendingTransferToNome
            };
        }

        private sealed class EstabelecimentoLocationRow
        {
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Cidade { get; set; }
            public string? Uf { get; set; }
        }

        public async Task<IReadOnlyCollection<MotoboyLocationHistoryPointDto>> GetLocationHistoryAsync(
            Guid estabelecimentoId,
            int motoboyId,
            DateOnly localDate)
        {
            await EnsureSchemaAsync();

            const string sql = @"
SELECT
    motoboy_id AS MotoboyId,
    latitude AS Latitude,
    longitude AS Longitude,
    accuracy_meters AS AccuracyMeters,
    speed_mps AS SpeedMps,
    heading_degrees AS HeadingDegrees,
    tracking_mode AS TrackingMode,
    quality AS Quality,
    client_timestamp_utc AS ClientTimestampUtc,
    server_received_at_utc AS ServerReceivedAtUtc,
    local_date AS LocalDate,
    sequence AS Sequence
FROM motoboy_location_history_daily
WHERE id_estabelecimento = @EstabelecimentoId
  AND motoboy_id = @MotoboyId
  AND local_date = @LocalDate
ORDER BY client_timestamp_utc ASC, id ASC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<MotoboyLocationHistoryPointDto>(sql, new
            {
                EstabelecimentoId = estabelecimentoId,
                MotoboyId = motoboyId,
                LocalDate = localDate
            });

            return rows.ToList();
        }
    }
}
