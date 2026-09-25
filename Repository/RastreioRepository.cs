using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Rastreio;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Fase 6. Tudo tolera o banco sem a migration 20260928_01: leituras devolvem padroes/vazio e o servico
    /// de avisos simplesmente nao encontra candidatos; escritas respondem 503 MIGRATION_PENDING.
    /// </summary>
    public sealed class RastreioRepository : IRastreioRepository
    {
        private const string Numeric = @"'^-?[0-9]+(\.[0-9]+)?$'";
        private static volatile bool _schemaKnown;
        private readonly NpgsqlDataSource _dataSource;

        public RastreioRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private static DeliveryDomainException MigrationPending() =>
            new(503, "MIGRATION_PENDING", "Os avisos de rastreio ainda nao foram habilitados neste ambiente (migration 20260928_01 pendente).");

        /// <summary>So cacheia o "sim": antes da migration reconsulta e a API segue como antes.</summary>
        private static async Task<bool> HasSchemaAsync(NpgsqlConnection connection)
        {
            if (_schemaKnown) return true;
            var present = await connection.ExecuteScalarAsync<bool>(@"
SELECT to_regclass('pedido_notificacao') IS NOT NULL AND to_regclass('pedido_rastreio_token') IS NOT NULL
   AND (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema()
         AND ((table_name = 'pedido' AND column_name = 'rastreio_opt_in')
           OR (table_name = 'motoboy' AND column_name = 'compartilhar_localizacao_cliente')
           OR (table_name = 'delivery_settings' AND column_name = 'notify_arriving_minutes'))) = 3;");
            if (present) _schemaKnown = true;
            return present;
        }

        // =====================================================================
        // Configuracao
        // =====================================================================

        private sealed class SettingsRow
        {
            public bool DispatchEnabled { get; set; }
            public bool ArrivingEnabled { get; set; }
            public int ArrivingMinutes { get; set; }
            public int ArrivingRadiusM { get; set; }
            public string? TemplateDispatch { get; set; }
            public string? TemplateArriving { get; set; }
        }

        public async Task<NoticeSettings> GetSettingsAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) return new NoticeSettings();
            var row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(@"
SELECT notify_dispatch_enabled AS DispatchEnabled, notify_arriving_enabled AS ArrivingEnabled,
       notify_arriving_minutes AS ArrivingMinutes, notify_arriving_radius_m AS ArrivingRadiusM,
       notify_template_dispatch AS TemplateDispatch, notify_template_arriving AS TemplateArriving
  FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;", new { EstabelecimentoId = estabelecimentoId });
            return row == null
                ? new NoticeSettings()
                : new NoticeSettings
                {
                    DispatchEnabled = row.DispatchEnabled, ArrivingEnabled = row.ArrivingEnabled,
                    ArrivingMinutes = row.ArrivingMinutes, ArrivingRadiusM = row.ArrivingRadiusM,
                    TemplateDispatch = row.TemplateDispatch, TemplateArriving = row.TemplateArriving
                };
        }

        public async Task<NoticeSettings> UpsertSettingsAsync(Guid estabelecimentoId, int actorUserId, NoticeSettings settings)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            await connection.ExecuteAsync(@"
INSERT INTO delivery_settings (estabelecimento_id, updated_by_user_id, updated_at_utc,
    notify_dispatch_enabled, notify_arriving_enabled, notify_arriving_minutes, notify_arriving_radius_m,
    notify_template_dispatch, notify_template_arriving)
VALUES (@EstabelecimentoId, @ActorUserId, NOW(), @DispatchEnabled, @ArrivingEnabled, @ArrivingMinutes, @ArrivingRadiusM,
    @TemplateDispatch, @TemplateArriving)
ON CONFLICT (estabelecimento_id) DO UPDATE SET
    notify_dispatch_enabled = EXCLUDED.notify_dispatch_enabled,
    notify_arriving_enabled = EXCLUDED.notify_arriving_enabled,
    notify_arriving_minutes = EXCLUDED.notify_arriving_minutes,
    notify_arriving_radius_m = EXCLUDED.notify_arriving_radius_m,
    notify_template_dispatch = EXCLUDED.notify_template_dispatch,
    notify_template_arriving = EXCLUDED.notify_template_arriving,
    updated_by_user_id = EXCLUDED.updated_by_user_id,
    updated_at_utc = NOW();",
                new
                {
                    EstabelecimentoId = estabelecimentoId, ActorUserId = actorUserId,
                    settings.DispatchEnabled, settings.ArrivingEnabled, settings.ArrivingMinutes, settings.ArrivingRadiusM,
                    settings.TemplateDispatch, settings.TemplateArriving
                });
            return await GetSettingsAsync(estabelecimentoId);
        }

        // =====================================================================
        // Opt-in, historico e autorizacao do motoboy
        // =====================================================================

        public async Task<bool> SetOptInAsync(Guid estabelecimentoId, int pedidoId, bool optIn, string origem)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            return await connection.ExecuteAsync(@"
UPDATE pedido
   SET rastreio_opt_in = @OptIn,
       rastreio_opt_in_em = CASE WHEN @OptIn THEN NOW() ELSE NULL END,
       rastreio_opt_in_origem = CASE WHEN @OptIn THEN @Origem ELSE NULL END
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { OptIn = optIn, Origem = origem, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }) > 0;
        }

        public async Task<RastreioPedidoDto> GetPedidoAsync(Guid estabelecimentoId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var dto = new RastreioPedidoDto();
            if (!await HasSchemaAsync(connection)) return dto;

            var optIn = await connection.ExecuteScalarAsync<bool?>(
                "SELECT rastreio_opt_in FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId })
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            dto.OptIn = optIn;
            dto.Avisos = (await connection.QueryAsync<AvisoDto>(@"
SELECT tipo AS Tipo, status AS Status, motivo AS Motivo, criada_em AS CriadaEm, enviada_em AS EnviadaEm
  FROM pedido_notificacao WHERE pedido_id = @PedidoId ORDER BY id;", new { PedidoId = pedidoId })).ToList();
            return dto;
        }

        public async Task<bool> SetMotoboySharingAsync(int motoboyId, bool shares)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            return await connection.ExecuteAsync(
                "UPDATE motoboy SET compartilhar_localizacao_cliente = @Shares WHERE id = @MotoboyId OR canonical_motoboy_id = @MotoboyId;",
                new { Shares = shares, MotoboyId = motoboyId }) > 0;
        }

        public async Task<bool> GetMotoboySharingAsync(int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) return false;
            return await connection.ExecuteScalarAsync<bool?>(
                "SELECT compartilhar_localizacao_cliente FROM motoboy WHERE id = @MotoboyId;", new { MotoboyId = motoboyId }) ?? false;
        }

        /// <summary>Reserva o disparo (um por tipo e pedido). Devolve o id, ou null se outro processo ja reservou.</summary>
        public async Task<long?> TryReserveAsync(int pedidoId, string tipo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            return await connection.ExecuteScalarAsync<long?>(@"
INSERT INTO pedido_notificacao (pedido_id, tipo, status) VALUES (@PedidoId, @Tipo, 'pendente')
ON CONFLICT (pedido_id, tipo) DO NOTHING RETURNING id;", new { PedidoId = pedidoId, Tipo = tipo });
        }

        public async Task MarkAsync(long id, string status, string? motivo, Guid? mensagemId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE pedido_notificacao
   SET status = @Status, motivo = @Motivo, mensagem_id = @MensagemId,
       enviada_em = CASE WHEN @Status = 'enviada' THEN NOW() ELSE enviada_em END
 WHERE id = @Id;", new { Id = id, Status = status, Motivo = motivo == null ? null : (motivo.Length > 300 ? motivo[..300] : motivo), MensagemId = mensagemId });
        }

        /// <summary>Libera um aviso que falhou/foi ignorado para o atendente reenviar. Aviso enviado nao se repete.</summary>
        public async Task<bool> ReleaseForResendAsync(Guid estabelecimentoId, int pedidoId, string tipo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            return await connection.ExecuteAsync(@"
DELETE FROM pedido_notificacao n USING pedido p
 WHERE n.pedido_id = p.id AND p.id_estabelecimento = @EstabelecimentoId
   AND n.pedido_id = @PedidoId AND n.tipo = @Tipo AND n.status IN ('falhou', 'ignorada');",
                new { EstabelecimentoId = estabelecimentoId, PedidoId = pedidoId, Tipo = tipo }) > 0;
        }

        // =====================================================================
        // Candidatos (servico de avisos)
        // =====================================================================

        public async Task<IReadOnlyList<NoticeCandidate>> GetCandidatesAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) return Array.Empty<NoticeCandidate>();

            var rows = await connection.QueryAsync<CandidateRow>($@"
SELECT p.id AS PedidoId, p.id_estabelecimento AS EstabelecimentoId,
       p.nome_cliente::text AS NomeCliente,
       CASE WHEN p.latitude::text ~ {Numeric} THEN p.latitude::text::DOUBLE PRECISION END AS DestLat,
       CASE WHEN p.longitude::text ~ {Numeric} THEN p.longitude::text::DOUBLE PRECISION END AS DestLon,
       m.id AS MotoboyId, m.nome::text AS MotoboyNome,
       COALESCE(m.compartilhar_localizacao_cliente, FALSE) AS Shares,
       e.nome_fantasia::text AS Loja,
       EXISTS (SELECT 1 FROM pedido_notificacao n WHERE n.pedido_id = p.id AND n.tipo = 'saiu_da_loja') AS DispatchDone,
       EXISTS (SELECT 1 FROM pedido_notificacao n WHERE n.pedido_id = p.id AND n.tipo = 'motoboy_chegando') AS ArrivingDone,
       COALESCE(s.notify_dispatch_enabled, TRUE) AS DispatchEnabled,
       COALESCE(s.notify_arriving_enabled, TRUE) AS ArrivingEnabled,
       COALESCE(s.notify_arriving_minutes, 5) AS ArrivingMinutes,
       COALESCE(s.notify_arriving_radius_m, 400) AS ArrivingRadiusM,
       s.notify_template_dispatch AS TemplateDispatch, s.notify_template_arriving AS TemplateArriving,
       lc.latitude AS MotoLat, lc.longitude AS MotoLon, lc.speed_mps AS SpeedMps,
       EXTRACT(EPOCH FROM (NOW() - lc.received_at_utc))::double precision AS AgeSeconds
  FROM pedido p
  JOIN motoboy m ON m.id = p.motoboy_responsavel
  JOIN estabelecimentos e ON e.id = p.id_estabelecimento
  LEFT JOIN delivery_settings s ON s.estabelecimento_id = p.id_estabelecimento
  LEFT JOIN LATERAL (
      SELECT c.latitude, c.longitude, c.speed_mps, c.received_at_utc
        FROM motoboy_location_current c
       WHERE c.motoboy_id = m.id OR c.motoboy_id = m.canonical_motoboy_id
       ORDER BY c.received_at_utc DESC LIMIT 1
  ) lc ON TRUE
 WHERE p.status_pedido = 2 AND p.rastreio_opt_in = TRUE
   AND NOT (EXISTS (SELECT 1 FROM pedido_notificacao n WHERE n.pedido_id = p.id AND n.tipo = 'saiu_da_loja')
        AND EXISTS (SELECT 1 FROM pedido_notificacao n WHERE n.pedido_id = p.id AND n.tipo = 'motoboy_chegando'))
 ORDER BY p.id
 LIMIT 500;");

            return rows.Select(row => new NoticeCandidate
            {
                PedidoId = row.PedidoId,
                EstabelecimentoId = row.EstabelecimentoId,
                NomeCliente = row.NomeCliente,
                Loja = row.Loja,
                MotoboyId = row.MotoboyId,
                MotoboyNome = row.MotoboyNome,
                MotoboyShares = row.Shares,
                DispatchDone = row.DispatchDone,
                ArrivingDone = row.ArrivingDone,
                Settings = new NoticeSettings
                {
                    DispatchEnabled = row.DispatchEnabled, ArrivingEnabled = row.ArrivingEnabled,
                    ArrivingMinutes = row.ArrivingMinutes, ArrivingRadiusM = row.ArrivingRadiusM,
                    TemplateDispatch = row.TemplateDispatch, TemplateArriving = row.TemplateArriving
                },
                DistanceMeters = row.DestLat.HasValue && row.DestLon.HasValue && row.MotoLat.HasValue && row.MotoLon.HasValue
                    ? OrderCoreRules.DistanceKm(row.MotoLat.Value, row.MotoLon.Value, row.DestLat.Value, row.DestLon.Value) * 1000d
                    : null,
                LocationAgeSeconds = row.AgeSeconds,
                SpeedMps = row.SpeedMps
            }).ToList();
        }

        private sealed class CandidateRow
        {
            public int PedidoId { get; set; }
            public Guid EstabelecimentoId { get; set; }
            public string? NomeCliente { get; set; }
            public double? DestLat { get; set; }
            public double? DestLon { get; set; }
            public int MotoboyId { get; set; }
            public string? MotoboyNome { get; set; }
            public bool Shares { get; set; }
            public string? Loja { get; set; }
            public bool DispatchDone { get; set; }
            public bool ArrivingDone { get; set; }
            public bool DispatchEnabled { get; set; }
            public bool ArrivingEnabled { get; set; }
            public int ArrivingMinutes { get; set; }
            public int ArrivingRadiusM { get; set; }
            public string? TemplateDispatch { get; set; }
            public string? TemplateArriving { get; set; }
            public double? MotoLat { get; set; }
            public double? MotoLon { get; set; }
            public double? SpeedMps { get; set; }
            public double? AgeSeconds { get; set; }
        }

        // =====================================================================
        // Link publico
        // =====================================================================

        /// <summary>Token valido do pedido: reaproveita o existente; cria (ou renova) se nao ha ou expirou.</summary>
        public async Task<string> EnsureTokenAsync(int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) throw MigrationPending();
            var existing = await connection.ExecuteScalarAsync<string?>(@"
SELECT token FROM pedido_rastreio_token
 WHERE pedido_id = @PedidoId AND invalidado_em IS NULL AND expira_em > NOW();", new { PedidoId = pedidoId });
            if (existing != null) return existing;

            var token = RastreioToken.Generate();
            await connection.ExecuteAsync(@"
INSERT INTO pedido_rastreio_token (pedido_id, token, expira_em)
VALUES (@PedidoId, @Token, NOW() + @Ttl)
ON CONFLICT (pedido_id) DO UPDATE SET token = EXCLUDED.token, criado_em = NOW(), expira_em = EXCLUDED.expira_em, invalidado_em = NULL;",
                new { PedidoId = pedidoId, Token = token, Ttl = RastreioToken.Lifetime });
            return token;
        }

        public async Task<PublicTrackingSource?> GetPublicSourceAsync(string token)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await HasSchemaAsync(connection)) return null;

            var row = await connection.QuerySingleOrDefaultAsync<PublicRow>($@"
SELECT t.pedido_id AS PedidoId, (t.expira_em <= NOW() OR t.invalidado_em IS NOT NULL) AS Expirado,
       COALESCE(p.status_pedido, 1) AS StatusPedido, p.previsao_entrega::text AS PrevisaoRaw, p.data_pedido::text AS DataPedidoRaw,
       CASE WHEN p.latitude::text ~ {Numeric} THEN p.latitude::text::DOUBLE PRECISION END AS DestLat,
       CASE WHEN p.longitude::text ~ {Numeric} THEN p.longitude::text::DOUBLE PRECISION END AS DestLon,
       e.nome_fantasia::text AS Loja, e.latitude::double precision AS LojaLat, e.longitude::double precision AS LojaLon,
       m.nome::text AS MotoboyNome, COALESCE(m.compartilhar_localizacao_cliente, FALSE) AS Shares,
       lc.latitude AS MotoLat, lc.longitude AS MotoLon,
       EXTRACT(EPOCH FROM (NOW() - lc.received_at_utc))::double precision AS AgeSeconds
  FROM pedido_rastreio_token t
  JOIN pedido p ON p.id = t.pedido_id
  JOIN estabelecimentos e ON e.id = p.id_estabelecimento
  LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
  LEFT JOIN LATERAL (
      SELECT c.latitude, c.longitude, c.received_at_utc FROM motoboy_location_current c
       WHERE m.id IS NOT NULL AND (c.motoboy_id = m.id OR c.motoboy_id = m.canonical_motoboy_id)
       ORDER BY c.received_at_utc DESC LIMIT 1
  ) lc ON TRUE
 WHERE t.token = @Token;", new { Token = token });
            if (row == null) return null;

            return new PublicTrackingSource
            {
                PedidoId = row.PedidoId,
                Expired = row.Expirado,
                StatusPedido = row.StatusPedido,
                Previsao = DeliveryRules.ParseStoredDateTime(row.PrevisaoRaw, DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw)),
                DestLat = row.DestLat, DestLon = row.DestLon,
                Loja = row.Loja, LojaLat = row.LojaLat, LojaLon = row.LojaLon,
                MotoboyNome = row.MotoboyNome, MotoboyShares = row.Shares,
                MotoLat = row.MotoLat, MotoLon = row.MotoLon, LocationAgeSeconds = row.AgeSeconds
            };
        }

        public async Task InvalidateTokenAsync(int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(
                "UPDATE pedido_rastreio_token SET invalidado_em = COALESCE(invalidado_em, NOW()) WHERE pedido_id = @PedidoId;",
                new { PedidoId = pedidoId });
        }

        private sealed class PublicRow
        {
            public int PedidoId { get; set; }
            public bool Expirado { get; set; }
            public int StatusPedido { get; set; }
            public string? PrevisaoRaw { get; set; }
            public string? DataPedidoRaw { get; set; }
            public double? DestLat { get; set; }
            public double? DestLon { get; set; }
            public string? Loja { get; set; }
            public double? LojaLat { get; set; }
            public double? LojaLon { get; set; }
            public string? MotoboyNome { get; set; }
            public bool Shares { get; set; }
            public double? MotoLat { get; set; }
            public double? MotoLon { get; set; }
            public double? AgeSeconds { get; set; }
        }
    }
}
