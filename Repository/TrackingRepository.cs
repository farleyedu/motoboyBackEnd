using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Tracking;
using APIBack.Model.Tracking;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class TrackingRepository : ITrackingRepository
    {
        private static readonly SemaphoreSlim SchemaLock = new(1, 1);
        private static bool _schemaEnsured;

        private readonly string _connectionString;

        public TrackingRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection nao configurada.");
        }

        public async Task EnsureSchemaAsync()
        {
            if (_schemaEnsured)
            {
                return;
            }

            await SchemaLock.WaitAsync();
            try
            {
                if (_schemaEnsured)
                {
                    return;
                }

                const string sql = @"
ALTER TABLE IF EXISTS motoboy
    ADD COLUMN IF NOT EXISTS id_usuario INTEGER,
    ADD COLUMN IF NOT EXISTS id_estabelecimento UUID;

ALTER TABLE IF EXISTS pedido
    ADD COLUMN IF NOT EXISTS id_estabelecimento UUID;

CREATE TABLE IF NOT EXISTS motoboy_location_state (
    motoboy_id INTEGER PRIMARY KEY,
    id_estabelecimento UUID NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    last_sequence BIGINT NULL,
    client_timestamp_utc TIMESTAMPTZ NOT NULL,
    server_received_at_utc TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS motoboy_location_history_daily (
    id BIGSERIAL PRIMARY KEY,
    motoboy_id INTEGER NOT NULL,
    id_estabelecimento UUID NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    client_timestamp_utc TIMESTAMPTZ NOT NULL,
    server_received_at_utc TIMESTAMPTZ NOT NULL,
    local_date DATE NOT NULL,
    sequence BIGINT NULL
);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_state_estabelecimento
    ON motoboy_location_state (id_estabelecimento);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_history_daily_lookup
    ON motoboy_location_history_daily (id_estabelecimento, motoboy_id, local_date, client_timestamp_utc);

CREATE INDEX IF NOT EXISTS ix_motoboy_id_usuario
    ON motoboy (id_usuario);

CREATE INDEX IF NOT EXISTS ix_motoboy_id_estabelecimento
    ON motoboy (id_estabelecimento);

CREATE INDEX IF NOT EXISTS ix_pedido_id_estabelecimento
    ON pedido (id_estabelecimento);
";

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.ExecuteAsync(sql);
                _schemaEnsured = true;
            }
            finally
            {
                SchemaLock.Release();
            }
        }

        public async Task<MotoboyTrackingIdentity?> ResolveMotoboyForUserAsync(int userId, Guid estabelecimentoId)
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
WHERE
    (m.id_usuario = @UserId OR (m.id_usuario IS NULL AND m.id = @UserId))
    AND (m.id_estabelecimento IS NULL OR m.id_estabelecimento = @EstabelecimentoId)
ORDER BY
    CASE WHEN m.id_usuario = @UserId THEN 0 ELSE 1 END,
    m.id
LIMIT 1;";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<MotoboyTrackingIdentity>(
                sql,
                new { UserId = userId, EstabelecimentoId = estabelecimentoId });
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

            const string pedidosSql = @"
SELECT
    p.id AS Id,
    p.nome_cliente AS NomeCliente,
    p.id_ifood AS IdIfood,
    p.telefone_cliente AS TelefoneCliente,
    p.data_pedido AS DataPedido,
    p.endereco_entrega AS EnderecoEntrega,
    p.items AS Items,
    p.value AS Value,
    p.region AS Region,
    CASE COALESCE(p.status_pedido, 1)
        WHEN 1 THEN 'pendente'
        WHEN 2 THEN 'em_rota'
        WHEN 3 THEN 'concluido'
        WHEN 4 THEN 'cancelado'
        ELSE 'pendente'
    END AS StatusPedido,
    p.motoboy_responsavel AS AssignedDriver,
    CASE WHEN p.latitude ~ '^-?[0-9]+(\.[0-9]+)?$' THEN p.latitude::DOUBLE PRECISION ELSE NULL END AS Latitude,
    CASE WHEN p.longitude ~ '^-?[0-9]+(\.[0-9]+)?$' THEN p.longitude::DOUBLE PRECISION ELSE NULL END AS Longitude,
    p.horario_pedido AS HorarioPedido,
    p.previsao_entrega AS PrevisaoEntrega,
    p.horario_saida AS HorarioSaida,
    p.horario_entrega AS HorarioEntrega
FROM pedido p
WHERE p.id_estabelecimento = @EstabelecimentoId
   OR p.id_estabelecimento IS NULL
ORDER BY p.data_pedido DESC NULLS LAST, p.id DESC;";

            await using var connection = new NpgsqlConnection(_connectionString);
            var motoboys = (await connection.QueryAsync<MotoboyMapDto>(motoboysSql, new { EstabelecimentoId = estabelecimentoId })).ToList();
            var pedidos = (await connection.QueryAsync<OrderMapDto>(pedidosSql, new { EstabelecimentoId = estabelecimentoId })).ToList();

            var pedidosPorMotoboy = pedidos
                .Where(p => p.AssignedDriver.HasValue)
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
                Motoboys = motoboys,
                Pedidos = pedidos
            };
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
