using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public sealed class PedidoQueueRepository : IPedidoQueueRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public PedidoQueueRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        private sealed class PedidoRow
        {
            public int Id { get; set; }
            public int? StatusPedido { get; set; }
            public int? MotoboyResponsavel { get; set; }
        }

        private sealed class EligibilityRow
        {
            public bool HasLink { get; set; }
            public Guid? SessionId { get; set; }
        }

        private sealed class StopRow
        {
            public long Id { get; set; }
            public int PedidoId { get; set; }
            public int Position { get; set; }
            public string StopStatus { get; set; } = "assigned";
            public DateTimeOffset AssignedAtUtc { get; set; }
        }

        public async Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var pedido = await connection.QuerySingleOrDefaultAsync<PedidoRow>(
                "SELECT id AS Id, status_pedido AS StatusPedido, motoboy_responsavel AS MotoboyResponsavel " +
                "FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId FOR UPDATE;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var currentStatus = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;

            // Idempotencia: repetir a mesma atribuicao para o mesmo motoboy devolve o estado atual.
            if ((currentStatus == StatusPedido.Atribuido || currentStatus == StatusPedido.EmRota)
                && pedido.MotoboyResponsavel == motoboyId)
            {
                await transaction.CommitAsync();
                return await GetQueueAsync(estabelecimentoId, motoboyId);
            }

            if (currentStatus == StatusPedido.Atribuido && pedido.MotoboyResponsavel.HasValue && pedido.MotoboyResponsavel != motoboyId)
            {
                // Pedido ainda esperando a vez (nao em rota) com outro motoboy: mover de fila
                // e permitido (usado pelo atendente ao editar uma rota antes de sair para entrega).
                var oldMotoboyId = pedido.MotoboyResponsavel.Value;
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() " +
                    "WHERE pedido_id = @PedidoId AND stop_status = 'assigned';",
                    new { PedidoId = pedidoId }, transaction);
                await RenumberActiveStopsAsync(connection, transaction, oldMotoboyId);
                await BumpRouteVersionAsync(connection, transaction, oldMotoboyId);
                currentStatus = StatusPedido.Pendente;
            }
            else if (currentStatus != StatusPedido.Pendente)
            {
                throw new DeliveryDomainException(409, "PEDIDO_NOT_ASSIGNABLE",
                    "Pedido em rota com outro motoboy nao pode ser reatribuido diretamente; conclua ou cancele a entrega atual primeiro.");
            }

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (!eligibility.HasLink)
            {
                throw new DeliveryDomainException(403, "LINK_FORBIDDEN", "Motoboy sem vinculo ativo com o estabelecimento.");
            }
            if (!eligibility.SessionId.HasValue)
            {
                throw new DeliveryDomainException(409, "MOTOBOY_NOT_ELIGIBLE", "Motoboy nao esta online neste estabelecimento.");
            }

            await EnsureRouteHeaderAsync(connection, transaction, motoboyId, estabelecimentoId);

            var hasCurrent = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status = 'en_route');",
                new { MotoboyId = motoboyId }, transaction);

            var nextPosition = await connection.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(position), 0) + 1 FROM delivery_route_stops " +
                "WHERE motoboy_id = @MotoboyId AND stop_status IN ('assigned','en_route');",
                new { MotoboyId = motoboyId }, transaction);

            var stopStatus = hasCurrent ? "assigned" : "en_route";
            var newPedidoStatus = hasCurrent ? StatusPedido.Atribuido : StatusPedido.EmRota;

            await connection.ExecuteAsync(
                "INSERT INTO delivery_route_stops " +
                "(estabelecimento_id, motoboy_id, pedido_id, position, stop_status, assigned_by_user_id, assigned_at_utc, started_at_utc, updated_at_utc) " +
                "VALUES (@EstabelecimentoId, @MotoboyId, @PedidoId, @Position, @StopStatus, @ActorUserId, NOW(), " +
                "CASE WHEN @StopStatus = 'en_route' THEN NOW() ELSE NULL END, NOW());",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    MotoboyId = motoboyId,
                    PedidoId = pedidoId,
                    Position = nextPosition,
                    StopStatus = stopStatus,
                    ActorUserId = actorUserId
                }, transaction);

            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Status, motoboy_responsavel = @MotoboyId, " +
                "horario_saida = CASE WHEN @StopStatus = 'en_route' THEN NOW() ELSE horario_saida END " +
                "WHERE id = @PedidoId;",
                new { Status = (int)newPedidoStatus, MotoboyId = motoboyId, StopStatus = stopStatus, PedidoId = pedidoId },
                transaction);

            var version = await BumpRouteVersionAsync(connection, transaction, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "assigned", version, eligibility.SessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var stop = await connection.QuerySingleOrDefaultAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops s " +
                "WHERE s.pedido_id = @PedidoId AND s.estabelecimento_id = @EstabelecimentoId " +
                "AND s.stop_status IN ('assigned','en_route') FOR UPDATE;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);

            if (stop == null)
            {
                throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_QUEUE", "Pedido nao esta na fila de nenhum motoboy.");
            }
            if (stop.StopStatus == "en_route")
            {
                throw new DeliveryDomainException(409, "STOP_NOT_REMOVABLE",
                    "A entrega atual (em rota) nao pode ser removida diretamente; use concluir ou cancelar.");
            }

            var motoboyId = await connection.ExecuteScalarAsync<int>(
                "SELECT motoboy_id FROM delivery_route_stops WHERE id = @Id;", new { stop.Id }, transaction);

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { stop.Id }, transaction);

            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Pendente, motoboy_responsavel = NULL WHERE id = @PedidoId;",
                new { Pendente = (int)StatusPedido.Pendente, PedidoId = pedidoId }, transaction);

            await RenumberActiveStopsAsync(connection, transaction, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, motoboyId);

            var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "removed", version, sessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> ReorderAsync(
            Guid estabelecimentoId, int actorUserId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await EnsureRouteHeaderAsync(connection, transaction, motoboyId, estabelecimentoId);
            var currentVersion = await connection.ExecuteScalarAsync<long>(
                "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId FOR UPDATE;",
                new { MotoboyId = motoboyId }, transaction);

            if (currentVersion != expectedVersion)
            {
                throw new DeliveryDomainException(409, "QUEUE_VERSION_CONFLICT",
                    "A fila mudou desde a ultima leitura. Recarregue e tente novamente.",
                    new { currentVersion });
            }

            var activeStops = (await connection.QueryAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status IN ('assigned','en_route') " +
                "ORDER BY position FOR UPDATE;",
                new { MotoboyId = motoboyId }, transaction)).ToList();

            var currentIds = activeStops.Select(s => s.PedidoId).ToHashSet();
            var requestedIds = pedidoIdsOrdenados.ToHashSet();
            if (!currentIds.SetEquals(requestedIds))
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH",
                    "A lista enviada nao corresponde exatamente aos pedidos ativos na fila deste motoboy.");
            }

            var currentStop = activeStops.FirstOrDefault(s => s.StopStatus == "en_route");
            if (currentStop != null && (pedidoIdsOrdenados.Count == 0 || pedidoIdsOrdenados[0] != currentStop.PedidoId))
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH",
                    "O pedido em rota (entrega atual) precisa continuar na primeira posicao.");
            }

            var position = 1;
            foreach (var pedidoIdInOrder in pedidoIdsOrdenados)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET position = @Position, updated_at_utc = NOW() " +
                    "WHERE motoboy_id = @MotoboyId AND pedido_id = @PedidoId AND stop_status IN ('assigned','en_route');",
                    new { Position = position, MotoboyId = motoboyId, PedidoId = pedidoIdInOrder }, transaction);
                position++;
            }

            var version = await BumpRouteVersionAsync(connection, transaction, motoboyId);
            var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "reordered", version, sessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var current = await connection.QuerySingleOrDefaultAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status = 'en_route' FOR UPDATE;",
                new { MotoboyId = motoboyId }, transaction)
                ?? throw new DeliveryDomainException(409, "NO_CURRENT_DELIVERY", "Este motoboy nao possui entrega atual em rota.");

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'completed', completed_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { current.Id }, transaction);
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Concluido, horario_entrega = NOW() WHERE id = @PedidoId;",
                new { Concluido = (int)StatusPedido.Concluido, current.PedidoId }, transaction);

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, motoboyId);

            var version = await BumpRouteVersionAsync(connection, transaction, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, current.PedidoId, "completed", version, eligibility.SessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (!eligibility.HasLink)
            {
                throw new DeliveryDomainException(403, "LINK_FORBIDDEN", "Motoboy sem vinculo ativo com o estabelecimento.");
            }
            if (!eligibility.SessionId.HasValue)
            {
                throw new DeliveryDomainException(409, "MOTOBOY_NOT_ELIGIBLE", "Motoboy nao esta online neste estabelecimento.");
            }

            var hasCurrent = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status = 'en_route' FOR UPDATE);",
                new { MotoboyId = motoboyId }, transaction);

            long version;
            if (!hasCurrent)
            {
                await TryPromoteNextAsync(connection, transaction, motoboyId);
                await RenumberActiveStopsAsync(connection, transaction, motoboyId);
                version = await BumpRouteVersionAsync(connection, transaction, motoboyId);
                await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "resumed", version, eligibility.SessionId);
            }
            else
            {
                version = await connection.ExecuteScalarAsync<long>(
                    "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId;",
                    new { MotoboyId = motoboyId }, transaction);
            }

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var pedido = await connection.QuerySingleOrDefaultAsync<PedidoRow>(
                "SELECT id AS Id, status_pedido AS StatusPedido, motoboy_responsavel AS MotoboyResponsavel " +
                "FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId FOR UPDATE;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");

            var currentStatus = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
            if (currentStatus == StatusPedido.Cancelado)
            {
                await transaction.CommitAsync();
                // Pedido cancelado sem motoboy responsavel nao tem fila para devolver;
                // GetQueueAsync com motoboyId 0 dispararia consultas sem dono.
                return pedido.MotoboyResponsavel is int responsavel && responsavel > 0
                    ? await GetQueueAsync(estabelecimentoId, responsavel)
                    : new MotoboyQueueDto { MotoboyId = 0, EstabelecimentoId = estabelecimentoId, Version = 0 };
            }
            if (currentStatus == StatusPedido.Concluido)
            {
                throw new DeliveryDomainException(409, "CANNOT_CANCEL_COMPLETED", "Pedido ja foi concluido e nao pode ser cancelado.");
            }

            var stop = await connection.QuerySingleOrDefaultAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops WHERE pedido_id = @PedidoId AND stop_status IN ('assigned','en_route') FOR UPDATE;",
                new { PedidoId = pedidoId }, transaction);

            var motoboyId = pedido.MotoboyResponsavel ?? 0;
            var wasEnRoute = stop?.StopStatus == "en_route";

            if (stop != null)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'canceled', canceled_at_utc = NOW(), cancel_reason = @Motivo, updated_at_utc = NOW() WHERE id = @Id;",
                    new { stop.Id, Motivo = motivo }, transaction);
            }

            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Cancelado WHERE id = @PedidoId;",
                new { Cancelado = (int)StatusPedido.Cancelado, PedidoId = pedidoId }, transaction);

            Guid? sessionId = null;
            long version = 0;
            if (stop != null && motoboyId > 0)
            {
                var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
                sessionId = eligibility.SessionId;
                if (wasEnRoute && eligibility.SessionId.HasValue)
                {
                    await TryPromoteNextAsync(connection, transaction, motoboyId);
                }
                await RenumberActiveStopsAsync(connection, transaction, motoboyId);
                version = await BumpRouteVersionAsync(connection, transaction, motoboyId);
                await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "canceled", version, sessionId);
            }

            var snapshot = motoboyId > 0
                ? await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version)
                : new MotoboyQueueDto { MotoboyId = 0, EstabelecimentoId = estabelecimentoId, Version = 0 };
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var version = await connection.ExecuteScalarAsync<long?>(
                "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId;",
                new { MotoboyId = motoboyId }, transaction) ?? 0;
            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        // ---- helpers ----

        private static async Task<EligibilityRow> GetEligibilityAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, Guid estabelecimentoId)
        {
            var row = await connection.QuerySingleOrDefaultAsync<EligibilityRow>(@"
SELECT
    EXISTS(
        SELECT 1 FROM motoboy alias
        JOIN motoboy canonical ON canonical.id = alias.canonical_motoboy_id
        JOIN motoboy_estabelecimento me ON me.motoboy_id = canonical.id
        WHERE alias.id = @MotoboyId AND me.estabelecimento_id = @EstabelecimentoId AND me.ativo = TRUE
    ) AS HasLink,
    (
        SELECT s.session_id
          FROM motoboy alias
          JOIN motoboy canonical ON canonical.id = alias.canonical_motoboy_id
          JOIN motoboy_active_sessions s ON s.motoboy_id = canonical.id
         WHERE alias.id = @MotoboyId
           AND s.id_estabelecimento = @EstabelecimentoId
           AND s.ended_at_utc IS NULL
           AND s.expires_at_utc > NOW()
         LIMIT 1
    ) AS SessionId;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            return row ?? new EligibilityRow();
        }

        private static async Task<Guid?> GetSessionIdAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, Guid estabelecimentoId)
        {
            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            return eligibility.SessionId;
        }

        private static Task EnsureRouteHeaderAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, Guid estabelecimentoId) =>
            connection.ExecuteAsync(
                "INSERT INTO delivery_motoboy_route (motoboy_id, estabelecimento_id, version, updated_at_utc) " +
                "VALUES (@MotoboyId, @EstabelecimentoId, 0, NOW()) ON CONFLICT (motoboy_id) DO NOTHING;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);

        private static async Task<long> BumpRouteVersionAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId) =>
            await connection.ExecuteScalarAsync<long>(
                "UPDATE delivery_motoboy_route SET version = version + 1, updated_at_utc = NOW() " +
                "WHERE motoboy_id = @MotoboyId RETURNING version;",
                new { MotoboyId = motoboyId }, transaction);

        /// <summary>
        /// Renumera as paradas ativas (assigned/en_route) do motoboy em ordem, comecando em 1,
        /// preservando a ordem relativa atual. Garante que nunca haja buraco/duplicidade de
        /// posicao apos remocao, conclusao, cancelamento ou promocao.
        /// </summary>
        private static async Task RenumberActiveStopsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId)
        {
            var ordered = (await connection.QueryAsync<(long Id, string StopStatus)>(
                "SELECT id, stop_status FROM delivery_route_stops " +
                "WHERE motoboy_id = @MotoboyId AND stop_status IN ('assigned','en_route') " +
                "ORDER BY (stop_status <> 'en_route'), position;",
                new { MotoboyId = motoboyId }, transaction)).ToList();

            var position = 1;
            foreach (var stop in ordered)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET position = @Position, updated_at_utc = NOW() WHERE id = @Id;",
                    new { Position = position, stop.Id }, transaction);
                position++;
            }
        }

        /// <summary>
        /// Promove o proximo pedido 'assigned' (menor posicao) para 'en_route' e o pedido
        /// correspondente para EmRota. So deve ser chamado quando o motoboy esta elegivel
        /// (online no mesmo estabelecimento) - quem chama ja validou isso.
        /// </summary>
        private static async Task TryPromoteNextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId)
        {
            var next = await connection.QuerySingleOrDefaultAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status = 'assigned' " +
                "ORDER BY position LIMIT 1 FOR UPDATE;",
                new { MotoboyId = motoboyId }, transaction);
            if (next == null) return;

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'en_route', started_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { next.Id }, transaction);
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @EmRota, horario_saida = NOW() WHERE id = @PedidoId;",
                new { EmRota = (int)StatusPedido.EmRota, next.PedidoId }, transaction);
        }

        private static async Task<MotoboyQueueDto> BuildSnapshotAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, long version)
        {
            var stops = (await connection.QueryAsync<StopRow>(
                "SELECT id AS Id, pedido_id AS PedidoId, position AS Position, stop_status AS StopStatus, assigned_at_utc AS AssignedAtUtc " +
                "FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status IN ('assigned','en_route') ORDER BY position;",
                new { MotoboyId = motoboyId }, transaction)).ToList();

            var current = stops.FirstOrDefault(s => s.StopStatus == "en_route");
            var next = stops.Where(s => s.StopStatus == "assigned").ToList();

            return new MotoboyQueueDto
            {
                MotoboyId = motoboyId,
                EstabelecimentoId = estabelecimentoId,
                Version = version,
                Current = current == null ? null : new RouteStopDto
                {
                    PedidoId = current.PedidoId,
                    Position = current.Position,
                    Status = "en_route",
                    AssignedAtUtc = current.AssignedAtUtc
                },
                Next = next.Select(s => new RouteStopDto
                {
                    PedidoId = s.PedidoId,
                    Position = s.Position,
                    Status = "assigned",
                    AssignedAtUtc = s.AssignedAtUtc
                }).ToList()
            };
        }

        private static async Task EmitQueueEventAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid estabelecimentoId,
            int motoboyId,
            int? pedidoId,
            string action,
            long version,
            Guid? sessionId)
        {
            var eventId = Guid.NewGuid();
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId,
                estabelecimentoId,
                motoboyId,
                pedidoId,
                action,
                version,
                occurredAtUtc
            });

            var targets = new List<string> { DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId) };
            if (sessionId.HasValue)
            {
                targets.Add(DeliveryRealtimeEvents.SessionGroup(sessionId.Value));
            }

            foreach (var target in targets)
            {
                await connection.ExecuteAsync(@"
INSERT INTO delivery_realtime_outbox (
    event_id, event_name, target_group, estabelecimento_id, motoboy_id,
    session_id, session_epoch, aggregate_version, payload, occurred_at_utc)
VALUES (
    @EventId, @EventName, @TargetGroup, @EstabelecimentoId, @MotoboyId,
    NULL, NULL, @AggregateVersion, @Payload::jsonb, @OccurredAtUtc);",
                    new
                    {
                        EventId = Guid.NewGuid(),
                        EventName = DeliveryRealtimeEvents.DeliveryQueueUpdated,
                        TargetGroup = target,
                        EstabelecimentoId = estabelecimentoId,
                        MotoboyId = motoboyId,
                        AggregateVersion = version,
                        Payload = payload,
                        OccurredAtUtc = occurredAtUtc
                    }, transaction);
            }
        }
    }
}
