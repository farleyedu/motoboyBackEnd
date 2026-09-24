using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Transferencia de pedido entre motoboys e parametros operacionais do
    /// estabelecimento (delivery_settings).
    ///
    /// Politica (delivery_settings.transfer_policy):
    /// - 'direct': o motoboy transfere e a troca acontece na hora;
    /// - 'establishment_approval': vira solicitacao pendente, executada so quando o
    ///   atendente aprova.
    /// O atendente sempre transfere direto (policy 'operator' no historico).
    /// A politica vigente fica gravada na solicitacao: mudar o parametro depois nao
    /// reinterpreta solicitacoes antigas.
    /// </summary>
    public sealed partial class PedidoQueueRepository
    {
        private const string TransferSelect = @"
SELECT t.id AS Id,
       t.estabelecimento_id AS EstabelecimentoId,
       t.pedido_id AS PedidoId,
       t.from_motoboy_id AS FromMotoboyId,
       mf.nome AS FromMotoboyNome,
       t.to_motoboy_id AS ToMotoboyId,
       mt.nome AS ToMotoboyNome,
       t.status AS Status,
       t.policy AS Policy,
       t.requested_by AS RequestedBy,
       t.reason AS Reason,
       t.requested_at_utc AS RequestedAtUtc,
       t.decided_at_utc AS DecidedAtUtc,
       t.decision_note AS DecisionNote,
       t.completed_at_utc AS CompletedAtUtc
  FROM delivery_transfer_requests t
  JOIN motoboy mf ON mf.id = t.from_motoboy_id
  JOIN motoboy mt ON mt.id = t.to_motoboy_id";

        private sealed class SettingsRow
        {
            public string? TransferPolicy { get; set; }
            public bool RequireDeliveryCode { get; set; }
            public bool AllowMotoboyReorder { get; set; }
            public bool AllowMotoboyRefuse { get; set; }
            public DateTimeOffset? UpdatedAtUtc { get; set; }
        }

        private sealed class TransferEventRow
        {
            public long Id { get; set; }
            public int PedidoId { get; set; }
            public int FromMotoboyId { get; set; }
            public int ToMotoboyId { get; set; }
            public string Status { get; set; } = TransferStatuses.PendingApproval;
        }

        // =====================================================================
        // Parametros
        // =====================================================================

        public async Task<DeliverySettingsDto> GetSettingsAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            await transaction.CommitAsync();
            await LoadSettingsExtrasAsync(settings, estabelecimentoId);
            return settings;
        }

        public async Task<DeliverySettingsDto> UpsertSettingsAsync(
            Guid estabelecimentoId, int actorUserId, UpdateDeliverySettingsRequest request)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO delivery_settings (
    estabelecimento_id, transfer_policy, require_delivery_code, allow_motoboy_reorder,
    allow_motoboy_refuse, updated_by_user_id, updated_at_utc)
VALUES (
    @EstabelecimentoId, @TransferPolicy, @RequireDeliveryCode, @AllowMotoboyReorder,
    @AllowMotoboyRefuse, @ActorUserId, NOW())
ON CONFLICT (estabelecimento_id) DO UPDATE SET
    transfer_policy = EXCLUDED.transfer_policy,
    require_delivery_code = EXCLUDED.require_delivery_code,
    allow_motoboy_reorder = EXCLUDED.allow_motoboy_reorder,
    allow_motoboy_refuse = EXCLUDED.allow_motoboy_refuse,
    updated_by_user_id = EXCLUDED.updated_by_user_id,
    updated_at_utc = NOW();",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    request.TransferPolicy,
                    request.RequireDeliveryCode,
                    request.AllowMotoboyReorder,
                    request.AllowMotoboyRefuse,
                    ActorUserId = actorUserId
                }, transaction);
            if (request.OrderWindow != null)
            {
                // Sem janela no pedido, a que estava salva continua valendo.
                await connection.ExecuteAsync(
                    "UPDATE delivery_settings SET order_window = @OrderWindow::jsonb WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { OrderWindow = OrderWindowRules.Serialize(request.OrderWindow), EstabelecimentoId = estabelecimentoId }, transaction);
            }
            if (request.DefaultDeliveryMinutes.HasValue)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_settings SET default_delivery_minutes = @Minutes WHERE estabelecimento_id = @EstabelecimentoId;",
                    new { Minutes = request.DefaultDeliveryMinutes.Value, EstabelecimentoId = estabelecimentoId }, transaction);
            }
            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            await transaction.CommitAsync();
            await LoadSettingsExtrasAsync(settings, estabelecimentoId);
            return settings;
        }

        /// <summary>
        /// Parametros lidos a parte, com tolerancia: se a migration da coluna ainda nao rodou, valem os
        /// padroes em vez de quebrar o comando. Tambem resolve a janela de pedidos para mostrar o efeito.
        /// </summary>
        private async Task LoadSettingsExtrasAsync(DeliverySettingsDto settings, Guid estabelecimentoId)
        {
            await using var extrasConnection = await _dataSource.OpenConnectionAsync();
            settings.OrderWindow = await OrderWindowStore.ReadAsync(extrasConnection, estabelecimentoId);
            settings.DefaultDeliveryMinutes = await OrderWindowStore.ReadDefaultDeliveryMinutesAsync(extrasConnection, estabelecimentoId)
                ?? ManualOrderRules.DefaultPrevisaoMinutos;
            var range = OrderWindowRules.Resolve(settings.OrderWindow, DateTimeOffset.UtcNow);
            settings.OrderWindowFromUtc = range.FromUtc;
            settings.OrderWindowToUtc = range.ToUtc;
        }

        /// <summary>Sem linha em delivery_settings valem os padroes (comportamento permissivo).</summary>
        private static async Task<DeliverySettingsDto> GetSettingsInternalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId)
        {
            var row = await connection.QuerySingleOrDefaultAsync<SettingsRow?>(@"
SELECT transfer_policy AS TransferPolicy,
       require_delivery_code AS RequireDeliveryCode,
       allow_motoboy_reorder AS AllowMotoboyReorder,
       allow_motoboy_refuse AS AllowMotoboyRefuse,
       updated_at_utc AS UpdatedAtUtc
  FROM delivery_settings
 WHERE estabelecimento_id = @EstabelecimentoId;",
                new { EstabelecimentoId = estabelecimentoId }, transaction);

            if (row == null)
            {
                return new DeliverySettingsDto { EstabelecimentoId = estabelecimentoId, IsDefault = true };
            }

            return new DeliverySettingsDto
            {
                EstabelecimentoId = estabelecimentoId,
                TransferPolicy = TransferPolicies.IsConfigurable(row.TransferPolicy) ? row.TransferPolicy! : TransferPolicies.Direct,
                RequireDeliveryCode = row.RequireDeliveryCode,
                AllowMotoboyReorder = row.AllowMotoboyReorder,
                AllowMotoboyRefuse = row.AllowMotoboyRefuse,
                UpdatedAtUtc = row.UpdatedAtUtc,
                IsDefault = false
            };
        }

        // =====================================================================
        // Consultas
        // =====================================================================

        /// <summary>Motoboys online no estabelecimento que podem receber um pedido.</summary>
        public async Task<IReadOnlyList<TransferTargetDto>> GetTransferTargetsAsync(Guid estabelecimentoId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<TransferTargetDto>($@"
SELECT s.motoboy_id AS MotoboyId,
       COALESCE(m.nome, '') AS Nome,
       m.avatar AS Avatar,
       EXISTS(SELECT 1 FROM delivery_route_stops rs WHERE rs.motoboy_id = s.motoboy_id AND rs.stop_status = 'en_route') AS HasCurrentDelivery,
       (SELECT COUNT(*)::int FROM delivery_route_stops rs
         WHERE rs.motoboy_id = s.motoboy_id AND rs.estabelecimento_id = @EstabelecimentoId
           AND rs.stop_status IN {ActiveStatusesSql}) AS QueueSize
  FROM motoboy_active_sessions s
  JOIN motoboy m ON m.id = s.motoboy_id
  JOIN motoboy_estabelecimento me
    ON me.motoboy_id = s.motoboy_id AND me.estabelecimento_id = s.id_estabelecimento AND me.ativo = TRUE
 WHERE s.id_estabelecimento = @EstabelecimentoId
   AND s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
   AND s.expires_at_utc > NOW()
   AND s.motoboy_id <> @MotoboyId
 ORDER BY m.nome, m.id;",
                new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId })).ToList();
        }

        public async Task<IReadOnlyList<TransferRequestDto>> ListTransfersAsync(Guid estabelecimentoId, string? status, int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<TransferRequestDto>(
                TransferSelect + @"
 WHERE t.estabelecimento_id = @EstabelecimentoId
   AND (@Status::text IS NULL OR t.status = @Status)
 ORDER BY t.requested_at_utc DESC
 LIMIT @Limit;",
                new { EstabelecimentoId = estabelecimentoId, Status = status, Limit = Math.Clamp(limit, 1, 200) })).ToList();
        }

        public async Task<IReadOnlyList<TransferRequestDto>> ListMotoboyTransfersAsync(Guid estabelecimentoId, int motoboyId, int limit)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await connection.QueryAsync<TransferRequestDto>(
                TransferSelect + @"
 WHERE t.estabelecimento_id = @EstabelecimentoId
   AND (t.from_motoboy_id = @MotoboyId OR t.to_motoboy_id = @MotoboyId)
 ORDER BY t.requested_at_utc DESC
 LIMIT @Limit;",
                new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId, Limit = Math.Clamp(limit, 1, 100) })).ToList();
        }

        // =====================================================================
        // Comandos
        // =====================================================================

        public async Task<TransferResultDto> RequestTransferByMotoboyAsync(
            Guid estabelecimentoId, int fromMotoboyId, int actorUserId, int pedidoId, int toMotoboyId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
            if (!TransferPolicies.AllowsMotoboyTransfer(settings.TransferPolicy))
            {
                throw new DeliveryDomainException(403, "TRANSFER_NOT_ALLOWED",
                    "Este estabelecimento nao permite que o motoboy transfira pedidos. Peca ao atendente.");
            }
            var fromVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, fromMotoboyId, toMotoboyId);

            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != fromMotoboyId)
            {
                throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_YOUR_QUEUE", "Este pedido nao esta na sua fila.");
            }
            await EnsureEligibleAsync(connection, transaction, toMotoboyId, estabelecimentoId);

            if (settings.TransferPolicy == TransferPolicies.EstablishmentApproval)
            {
                var alreadyPending = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM delivery_transfer_requests WHERE pedido_id = @PedidoId AND status = 'pending_approval');",
                    new { PedidoId = pedidoId }, transaction);
                if (alreadyPending)
                {
                    throw new DeliveryDomainException(409, "TRANSFER_ALREADY_PENDING",
                        "Ja existe uma transferencia deste pedido aguardando aprovacao.");
                }

                var pendingId = await InsertTransferAsync(connection, transaction, estabelecimentoId, pedidoId,
                    fromMotoboyId, toMotoboyId, TransferStatuses.PendingApproval, TransferPolicies.EstablishmentApproval,
                    "motoboy", actorUserId, motivo);
                await EmitTransferEventAsync(connection, transaction, estabelecimentoId,
                    new TransferEventRow { Id = pendingId, PedidoId = pedidoId, FromMotoboyId = fromMotoboyId, ToMotoboyId = toMotoboyId, Status = TransferStatuses.PendingApproval });

                var result = new TransferResultDto
                {
                    Transfer = await GetTransferAsync(connection, transaction, estabelecimentoId, pendingId),
                    SourceQueue = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, fromMotoboyId, fromVersion)
                };
                await transaction.CommitAsync();
                return result;
            }

            var directResult = await ExecuteNewTransferAsync(connection, transaction, estabelecimentoId, stop,
                fromMotoboyId, toMotoboyId, TransferPolicies.Direct, "motoboy", actorUserId, motivo);
            await transaction.CommitAsync();
            return directResult;
        }

        public async Task<TransferResultDto> TransferByOperatorAsync(
            Guid estabelecimentoId, int operatorUserId, int pedidoId, int toMotoboyId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_QUEUE", "Pedido nao esta na fila de nenhum motoboy.");
            var fromMotoboyId = preview.MotoboyId;
            if (fromMotoboyId == toMotoboyId)
            {
                throw new DeliveryDomainException(422, "TRANSFER_SAME_MOTOBOY", "O pedido ja esta com este motoboy.");
            }

            await LockQueuesAsync(connection, transaction, estabelecimentoId, fromMotoboyId, toMotoboyId);
            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != fromMotoboyId)
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila do pedido mudou. Recarregue e tente novamente.");
            }
            await EnsureEligibleAsync(connection, transaction, toMotoboyId, estabelecimentoId);

            // A decisao do atendente substitui qualquer pedido de troca que o motoboy tenha feito.
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId,
                "Substituida por transferencia feita pelo atendente.");

            var result = await ExecuteNewTransferAsync(connection, transaction, estabelecimentoId, stop,
                fromMotoboyId, toMotoboyId, TransferPolicies.Operator, "operator", operatorUserId, motivo, decidedByUserId: operatorUserId);
            await transaction.CommitAsync();
            return result;
        }

        public async Task<TransferResultDto> ApproveTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetTransferAsync(connection, transaction, estabelecimentoId, requestId);
            await LockQueuesAsync(connection, transaction, estabelecimentoId, preview.FromMotoboyId, preview.ToMotoboyId);
            var status = await connection.ExecuteScalarAsync<string?>(
                "SELECT status FROM delivery_transfer_requests WHERE id = @Id FOR UPDATE;", new { Id = requestId }, transaction);
            if (status != TransferStatuses.PendingApproval)
            {
                throw new DeliveryDomainException(409, "TRANSFER_NOT_PENDING", "Esta transferencia nao esta mais aguardando aprovacao.");
            }

            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, preview.PedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != preview.FromMotoboyId)
            {
                throw new DeliveryDomainException(409, "TRANSFER_STALE",
                    "O pedido nao esta mais na fila do motoboy de origem. Rejeite esta solicitacao.");
            }
            await EnsureEligibleAsync(connection, transaction, preview.ToMotoboyId, estabelecimentoId);

            await connection.ExecuteAsync(@"
UPDATE delivery_transfer_requests
   SET status = 'completed', decided_by_user_id = @UserId, decided_at_utc = NOW(),
       decision_note = @Note, completed_at_utc = NOW(), updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = requestId, UserId = operatorUserId, Note = observacao }, transaction);

            var result = await ExecuteTransferAsync(connection, transaction, estabelecimentoId, stop,
                preview.FromMotoboyId, preview.ToMotoboyId, requestId);
            await transaction.CommitAsync();
            return result;
        }

        public async Task<TransferRequestDto> RejectTransferAsync(Guid estabelecimentoId, int operatorUserId, long requestId, string? observacao)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var transfer = await CloseTransferAsync(connection, transaction, estabelecimentoId, requestId, TransferStatuses.Rejected,
                operatorUserId, observacao, requiredFromMotoboyId: null);
            await transaction.CommitAsync();
            return transfer;
        }

        public async Task<TransferRequestDto> CancelTransferByMotoboyAsync(Guid estabelecimentoId, int motoboyId, long requestId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var transfer = await CloseTransferAsync(connection, transaction, estabelecimentoId, requestId, TransferStatuses.Cancelled,
                decidedByUserId: null, "Cancelada pelo motoboy.", requiredFromMotoboyId: motoboyId);
            await transaction.CommitAsync();
            return transfer;
        }

        // =====================================================================
        // Internos
        // =====================================================================

        private async Task<TransferResultDto> ExecuteNewTransferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, StopRow stop,
            int fromMotoboyId, int toMotoboyId, string policy, string requestedBy, int? requestedByUserId, string? motivo,
            int? decidedByUserId = null)
        {
            var requestId = await InsertTransferAsync(connection, transaction, estabelecimentoId, stop.PedidoId,
                fromMotoboyId, toMotoboyId, TransferStatuses.Completed, policy, requestedBy, requestedByUserId, motivo,
                decidedByUserId);
            return await ExecuteTransferAsync(connection, transaction, estabelecimentoId, stop, fromMotoboyId, toMotoboyId, requestId);
        }

        /// <summary>
        /// Move a parada: origem fica 'transferred' (historico), destino ganha uma parada
        /// nova no fim da fila. Se o pedido ja tinha sido coletado, a coleta acompanha o
        /// pedido. As duas filas ja estao travadas por quem chama.
        /// </summary>
        private async Task<TransferResultDto> ExecuteTransferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, StopRow stop,
            int fromMotoboyId, int toMotoboyId, long requestId)
        {
            await connection.ExecuteAsync(@"
UPDATE delivery_route_stops
   SET stop_status = 'transferred', transferred_at_utc = NOW(), transfer_request_id = @RequestId, updated_at_utc = NOW()
 WHERE id = @Id;", new { stop.Id, RequestId = requestId }, transaction);

            var sourceQueue = await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, fromMotoboyId,
                stop.PedidoId, "transferred_out", promoteNext: stop.StopStatus == "en_route");

            var toVersion = await AppendStopAsync(connection, transaction, estabelecimentoId, toMotoboyId, stop.PedidoId,
                actorUserId: null, pickedUpAtUtc: stop.PickedUpAtUtc, transferRequestId: requestId);
            var toSession = await GetSessionIdAsync(connection, transaction, toMotoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, toMotoboyId, stop.PedidoId, "transferred_in", toVersion, toSession);
            var targetQueue = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, toMotoboyId, toVersion);

            await EmitTransferEventAsync(connection, transaction, estabelecimentoId, new TransferEventRow
            {
                Id = requestId,
                PedidoId = stop.PedidoId,
                FromMotoboyId = fromMotoboyId,
                ToMotoboyId = toMotoboyId,
                Status = TransferStatuses.Completed
            });

            return new TransferResultDto
            {
                Transfer = await GetTransferAsync(connection, transaction, estabelecimentoId, requestId),
                SourceQueue = sourceQueue,
                TargetQueue = targetQueue
            };
        }

        private async Task<TransferRequestDto> CloseTransferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, long requestId,
            string newStatus, int? decidedByUserId, string? note, int? requiredFromMotoboyId)
        {
            var row = await connection.QuerySingleOrDefaultAsync<TransferEventRow?>(@"
SELECT id AS Id, pedido_id AS PedidoId, from_motoboy_id AS FromMotoboyId, to_motoboy_id AS ToMotoboyId, status AS Status
  FROM delivery_transfer_requests
 WHERE id = @Id AND estabelecimento_id = @EstabelecimentoId
 FOR UPDATE;", new { Id = requestId, EstabelecimentoId = estabelecimentoId }, transaction)
                ?? throw new DeliveryDomainException(404, "TRANSFER_NOT_FOUND", "Transferencia nao encontrada.");

            if (requiredFromMotoboyId.HasValue && row.FromMotoboyId != requiredFromMotoboyId.Value)
            {
                throw new DeliveryDomainException(404, "TRANSFER_NOT_FOUND", "Transferencia nao encontrada.");
            }
            if (row.Status != TransferStatuses.PendingApproval)
            {
                throw new DeliveryDomainException(409, "TRANSFER_NOT_PENDING", "Esta transferencia nao esta mais aguardando aprovacao.");
            }

            await connection.ExecuteAsync(@"
UPDATE delivery_transfer_requests
   SET status = @Status, decided_by_user_id = @UserId, decided_at_utc = NOW(),
       decision_note = @Note, updated_at_utc = NOW()
 WHERE id = @Id;", new { Id = requestId, Status = newStatus, UserId = decidedByUserId, Note = note }, transaction);

            row.Status = newStatus;
            await EmitTransferEventAsync(connection, transaction, estabelecimentoId, row);
            return await GetTransferAsync(connection, transaction, estabelecimentoId, requestId);
        }

        private static async Task<long> InsertTransferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int pedidoId,
            int fromMotoboyId, int toMotoboyId, string status, string policy, string requestedBy, int? requestedByUserId,
            string? motivo, int? decidedByUserId = null)
        {
            var completed = status == TransferStatuses.Completed;
            return await connection.ExecuteScalarAsync<long>(@"
INSERT INTO delivery_transfer_requests (
    estabelecimento_id, pedido_id, from_motoboy_id, to_motoboy_id, status, policy,
    requested_by, requested_by_user_id, reason, requested_at_utc,
    decided_by_user_id, decided_at_utc, completed_at_utc, updated_at_utc)
VALUES (
    @EstabelecimentoId, @PedidoId, @FromMotoboyId, @ToMotoboyId, @Status, @Policy,
    @RequestedBy, @RequestedByUserId, @Reason, NOW(),
    @DecidedByUserId, CASE WHEN @Completed THEN NOW() END, CASE WHEN @Completed THEN NOW() END, NOW())
RETURNING id;",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    PedidoId = pedidoId,
                    FromMotoboyId = fromMotoboyId,
                    ToMotoboyId = toMotoboyId,
                    Status = status,
                    Policy = policy,
                    RequestedBy = requestedBy,
                    RequestedByUserId = requestedByUserId,
                    Reason = motivo,
                    DecidedByUserId = decidedByUserId,
                    Completed = completed
                }, transaction);
        }

        private static async Task<TransferRequestDto> GetTransferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, long requestId) =>
            await connection.QuerySingleOrDefaultAsync<TransferRequestDto?>(
                TransferSelect + " WHERE t.id = @Id AND t.estabelecimento_id = @EstabelecimentoId;",
                new { Id = requestId, EstabelecimentoId = estabelecimentoId }, transaction)
            ?? throw new DeliveryDomainException(404, "TRANSFER_NOT_FOUND", "Transferencia nao encontrada.");

        /// <summary>
        /// Pedido saiu da fila de origem (entregue, cancelado, removido, recusado, nao
        /// entregue ou transferido pelo atendente): solicitacoes pendentes perdem o
        /// sentido e sao canceladas automaticamente.
        /// </summary>
        private static async Task CancelPendingTransfersForPedidoAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int pedidoId, string note)
        {
            var cancelled = (await connection.QueryAsync<TransferEventRow>(@"
UPDATE delivery_transfer_requests
   SET status = 'cancelled', decision_note = @Note, decided_at_utc = NOW(), updated_at_utc = NOW()
 WHERE pedido_id = @PedidoId AND estabelecimento_id = @EstabelecimentoId AND status = 'pending_approval'
RETURNING id AS Id, pedido_id AS PedidoId, from_motoboy_id AS FromMotoboyId, to_motoboy_id AS ToMotoboyId, status AS Status;",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId, Note = note }, transaction)).ToList();

            foreach (var row in cancelled)
            {
                await EmitTransferEventAsync(connection, transaction, estabelecimentoId, row);
            }
        }

        private static async Task EmitTransferEventAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, TransferEventRow transfer)
        {
            var sessions = new List<Guid>();
            foreach (var motoboyId in new[] { transfer.FromMotoboyId, transfer.ToMotoboyId })
            {
                var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
                if (sessionId.HasValue) sessions.Add(sessionId.Value);
            }

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                transferId = transfer.Id,
                pedidoId = transfer.PedidoId,
                fromMotoboyId = transfer.FromMotoboyId,
                toMotoboyId = transfer.ToMotoboyId,
                status = transfer.Status,
                occurredAtUtc = DateTimeOffset.UtcNow
            });

            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryTransferUpdated,
                estabelecimentoId, transfer.FromMotoboyId, transfer.Id, payload, sessions);
        }
    }
}
