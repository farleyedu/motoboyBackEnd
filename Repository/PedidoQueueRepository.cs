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
    /// <summary>
    /// Fila/rota de entrega por motoboy E estabelecimento.
    ///
    /// Regras de concorrencia (valem para todos os comandos, inclusive os das
    /// classes parciais de acoes do motoboy e transferencia):
    /// 1. Toda mutacao de fila trava primeiro o cabecalho da rota
    ///    (delivery_motoboy_route, por motoboy+estabelecimento) com FOR UPDATE; quando
    ///    envolve duas filas, trava na ordem crescente de motoboy_id (sem deadlock).
    /// 2. So depois trava pedido/parada e revalida o estado lido.
    /// 3. A versao do cabecalho sobe a cada mudanca e vai no evento de tempo real.
    /// </summary>
    public sealed partial class PedidoQueueRepository : IPedidoQueueRepository
    {
        private const string ActiveStatusesSql = "('assigned','en_route')";
        private const string NumericPattern = @"'^-?[0-9]+(\.[0-9]+)?$'";

        private const string StopColumns = @"
       s.id AS Id,
       s.estabelecimento_id AS EstabelecimentoId,
       s.motoboy_id AS MotoboyId,
       s.pedido_id AS PedidoId,
       s.position AS Position,
       s.stop_status AS StopStatus,
       s.assigned_at_utc AS AssignedAtUtc,
       s.picked_up_at_utc AS PickedUpAtUtc,
       s.arrived_at_utc AS ArrivedAtUtc";

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
            public Guid EstabelecimentoId { get; set; }
            public int MotoboyId { get; set; }
            public int PedidoId { get; set; }
            public int Position { get; set; }
            public string StopStatus { get; set; } = "assigned";
            public DateTimeOffset AssignedAtUtc { get; set; }
            public DateTimeOffset? PickedUpAtUtc { get; set; }
            public DateTimeOffset? ArrivedAtUtc { get; set; }
        }

        // =====================================================================
        // Comandos do atendente
        // =====================================================================

        public async Task<MotoboyQueueDto> AssignAsync(Guid estabelecimentoId, int actorUserId, int motoboyId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            // Leitura sem trava so para descobrir quais filas travar (regra 1).
            var preview = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var previousOwner = preview.MotoboyResponsavel.HasValue && preview.MotoboyResponsavel != motoboyId
                ? preview.MotoboyResponsavel
                : null;
            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId, previousOwner);

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var currentStatus = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;

            // Idempotencia: repetir a mesma atribuicao devolve o estado atual.
            if ((currentStatus == StatusPedido.Atribuido || currentStatus == StatusPedido.EmRota)
                && pedido.MotoboyResponsavel == motoboyId)
            {
                var sameVersion = await GetVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
                var same = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, sameVersion);
                await transaction.CommitAsync();
                return same;
            }

            if (currentStatus == StatusPedido.Atribuido && pedido.MotoboyResponsavel.HasValue)
            {
                // Pedido esperando a vez na fila de outro motoboy: o atendente pode move-lo.
                // (Pedido em rota com outro motoboy usa transferencia, que trata a entrega atual.)
                var oldMotoboyId = pedido.MotoboyResponsavel.Value;
                if (previousOwner != oldMotoboyId)
                {
                    throw new DeliveryDomainException(409, "QUEUE_CHANGED",
                        "A fila do pedido mudou durante a operacao. Recarregue e tente novamente.");
                }

                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() " +
                    "WHERE pedido_id = @PedidoId AND estabelecimento_id = @EstabelecimentoId AND stop_status = 'assigned';",
                    new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);
                await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId,
                    "Pedido movido pelo atendente para outro motoboy.");
                await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, oldMotoboyId);
                var oldVersion = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, oldMotoboyId);
                var oldSession = await GetSessionIdAsync(connection, transaction, oldMotoboyId, estabelecimentoId);
                await EmitQueueEventAsync(connection, transaction, estabelecimentoId, oldMotoboyId, pedidoId, "removed", oldVersion, oldSession);
                currentStatus = StatusPedido.Pendente;
            }
            else if (currentStatus != StatusPedido.Pendente)
            {
                throw new DeliveryDomainException(409, "PEDIDO_NOT_ASSIGNABLE",
                    "Pedido em rota com outro motoboy nao pode ser reatribuido diretamente; use transferir, concluir ou cancelar.");
            }

            var eligibility = await EnsureEligibleAsync(connection, transaction, motoboyId, estabelecimentoId);
            var version = await AppendStopAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, actorUserId,
                pickedUpAtUtc: null, transferRequestId: null);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "assigned", version, eligibility.SessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> RemoveAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_QUEUE", "Pedido nao esta na fila de nenhum motoboy.");
            var motoboyId = preview.MotoboyId;
            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);

            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop == null || stop.MotoboyId != motoboyId)
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila do pedido mudou. Recarregue e tente novamente.");
            }
            if (stop.StopStatus == "en_route")
            {
                throw new DeliveryDomainException(409, "STOP_NOT_REMOVABLE",
                    "A entrega atual (em rota) nao pode ser removida diretamente; use concluir, cancelar ou transferir.");
            }

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { stop.Id }, transaction);
            await ReturnPedidoToPendingAsync(connection, transaction, pedidoId);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId,
                "Pedido removido da fila pelo atendente.");

            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
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
            var snapshot = await ReorderInternalAsync(connection, transaction, estabelecimentoId, motoboyId, expectedVersion, pedidoIdsOrdenados);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> CompleteCurrentAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var snapshot = await CompleteCurrentInternalAsync(connection, transaction, estabelecimentoId, motoboyId,
                completedBy: "operator", providedCode: null, enforceCode: false);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> ResumeAsync(Guid estabelecimentoId, int actorUserId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var snapshot = await ResumeInternalAsync(connection, transaction, estabelecimentoId, motoboyId);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> CancelAsync(Guid estabelecimentoId, int actorUserId, int pedidoId, string? motivo)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var previewStop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false);
            var motoboyId = previewStop?.MotoboyId ?? preview.MotoboyResponsavel ?? 0;
            if (motoboyId > 0)
            {
                await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            }

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var currentStatus = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
            if (currentStatus == StatusPedido.Cancelado)
            {
                // Idempotente: cancelar de novo devolve a fila atual (ou vazia, sem motoboy).
                var idempotent = motoboyId > 0
                    ? await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId,
                        await GetVersionAsync(connection, transaction, estabelecimentoId, motoboyId))
                    : EmptyQueue(estabelecimentoId);
                await transaction.CommitAsync();
                return idempotent;
            }
            if (currentStatus == StatusPedido.Concluido)
            {
                throw new DeliveryDomainException(409, "CANNOT_CANCEL_COMPLETED", "Pedido ja foi concluido e nao pode ser cancelado.");
            }

            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop != null && stop.MotoboyId != motoboyId)
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila do pedido mudou. Recarregue e tente novamente.");
            }

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
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, "Pedido cancelado.");

            if (stop == null || motoboyId <= 0)
            {
                await transaction.CommitAsync();
                return EmptyQueue(estabelecimentoId);
            }

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (wasEnRoute && eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "canceled", version, eligibility.SessionId);

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        public async Task<MotoboyQueueDto> GetQueueAsync(Guid estabelecimentoId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var version = await GetVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        // =====================================================================
        // Operacoes internas compartilhadas
        // =====================================================================

        private async Task<MotoboyQueueDto> ReorderInternalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            Guid estabelecimentoId, int motoboyId, long expectedVersion, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            if (currentVersion != expectedVersion)
            {
                throw new DeliveryDomainException(409, "QUEUE_VERSION_CONFLICT",
                    "A fila mudou desde a ultima leitura. Recarregue e tente novamente.",
                    new { currentVersion });
            }

            var activeStops = await GetActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true);
            var currentIds = activeStops.Select(s => s.PedidoId).ToHashSet();
            if (!currentIds.SetEquals(pedidoIdsOrdenados.ToHashSet()))
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH",
                    "A lista enviada nao corresponde exatamente aos pedidos ativos na fila deste motoboy.");
            }

            var currentStop = activeStops.FirstOrDefault(s => s.StopStatus == "en_route");
            if (currentStop != null && pedidoIdsOrdenados[0] != currentStop.PedidoId)
            {
                throw new DeliveryDomainException(422, "QUEUE_MISMATCH",
                    "O pedido em rota (entrega atual) precisa continuar na primeira posicao.");
            }

            // Pedidos travados sao ancoras: mantem a posicao que tem hoje na fila.
            var lockedIds = await GetLockedPedidoIdsAsync(connection, transaction, estabelecimentoId, motoboyId);
            RouteLockRules.ValidateReorder(
                activeStops.Select(s => new RouteSlot(s.PedidoId, lockedIds.Contains(s.PedidoId), s.StopStatus == "en_route")).ToList(),
                pedidoIdsOrdenados);

            await ApplyPositionsAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoIdsOrdenados);

            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "reordered", version, sessionId);
            return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        }

        private async Task<MotoboyQueueDto> CompleteCurrentInternalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            Guid estabelecimentoId, int motoboyId, string completedBy, string? providedCode, bool enforceCode)
        {
            await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var current = await GetCurrentStopAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: true)
                ?? throw new DeliveryDomainException(409, "NO_CURRENT_DELIVERY", "Este motoboy nao possui entrega atual em rota.");

            if (enforceCode)
            {
                var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);
                if (settings.RequireDeliveryCode)
                {
                    var expected = await connection.ExecuteScalarAsync<string?>(
                        "SELECT codigo_entrega::text FROM pedido WHERE id = @PedidoId;",
                        new { current.PedidoId }, transaction);
                    if (DeliveryRules.HasDeliveryCode(expected) && string.IsNullOrWhiteSpace(providedCode))
                    {
                        throw new DeliveryDomainException(422, "DELIVERY_CODE_REQUIRED",
                            "Este estabelecimento exige o codigo de entrega informado pelo cliente.");
                    }
                    if (!DeliveryRules.DeliveryCodeMatches(expected, providedCode))
                    {
                        throw new DeliveryDomainException(422, "DELIVERY_CODE_INVALID", "Codigo de entrega incorreto.");
                    }
                }
            }

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'completed', completed_at_utc = NOW(), completed_by = @CompletedBy, updated_at_utc = NOW() WHERE id = @Id;",
                new { current.Id, CompletedBy = completedBy }, transaction);
            var entregaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_entrega");
            await connection.ExecuteAsync(
                $"UPDATE pedido SET status_pedido = @Concluido, horario_entrega = {entregaNow} WHERE id = @PedidoId;",
                new { Concluido = (int)StatusPedido.Concluido, current.PedidoId }, transaction);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, current.PedidoId, "Pedido entregue.");

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var entersReturn = await EnterReturningIfIdleAsync(connection, transaction, estabelecimentoId, motoboyId);

            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, current.PedidoId,
                completedBy == "motoboy" ? "delivered" : "completed", version, eligibility.SessionId);
            if (entersReturn)
            {
                await EmitRouteStateEventAsync(connection, transaction, estabelecimentoId, motoboyId,
                    DeliveryRealtimeEvents.DeliveryRouteReturning, RouteStates.Returning, "delivery", version, eligibility.SessionId);
            }
            return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        }

        private async Task<MotoboyQueueDto> ResumeInternalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var eligibility = await EnsureEligibleAsync(connection, transaction, motoboyId, estabelecimentoId);

            var hasCurrentHere = await GetCurrentStopAsync(connection, transaction, estabelecimentoId, motoboyId, forUpdate: false) != null;
            if (hasCurrentHere)
            {
                return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, currentVersion);
            }

            await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "resumed", version, eligibility.SessionId);
            return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        }

        /// <summary>
        /// Coloca o pedido no fim da fila do motoboy. Vira entrega atual se o motoboy nao
        /// tem nenhuma em andamento (em qualquer estabelecimento: o indice garante uma
        /// entrega em rota por motoboy). A fila precisa estar travada por quem chama.
        /// </summary>
        private static async Task<long> AppendStopAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            Guid estabelecimentoId, int motoboyId, int pedidoId, int? actorUserId,
            DateTimeOffset? pickedUpAtUtc, long? transferRequestId)
        {
            var hasCurrent = await HasEnRouteAnywhereAsync(connection, transaction, motoboyId);
            var nextPosition = await connection.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(position), 0) + 1 FROM delivery_route_stops " +
                $"WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN {ActiveStatusesSql};",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);

            var stopStatus = hasCurrent ? "assigned" : "en_route";
            var newPedidoStatus = hasCurrent ? StatusPedido.Atribuido : StatusPedido.EmRota;

            await connection.ExecuteAsync(@"
INSERT INTO delivery_route_stops
    (estabelecimento_id, motoboy_id, pedido_id, position, stop_status, assigned_by_user_id,
     assigned_at_utc, started_at_utc, picked_up_at_utc, transfer_request_id, updated_at_utc)
VALUES
    (@EstabelecimentoId, @MotoboyId, @PedidoId, @Position, @StopStatus, @ActorUserId,
     NOW(), CASE WHEN @StopStatus = 'en_route' THEN NOW() ELSE NULL END, @PickedUpAtUtc, @TransferRequestId, NOW());",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    MotoboyId = motoboyId,
                    PedidoId = pedidoId,
                    Position = nextPosition,
                    StopStatus = stopStatus,
                    ActorUserId = actorUserId,
                    PickedUpAtUtc = pickedUpAtUtc,
                    TransferRequestId = transferRequestId
                }, transaction);

            await ClearReturningAsync(connection, transaction, estabelecimentoId, motoboyId);

            var saidaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_saida");
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Status, motoboy_responsavel = @MotoboyId, " +
                $"horario_saida = CASE WHEN @StopStatus = 'en_route' THEN {saidaNow} ELSE NULL END " +
                "WHERE id = @PedidoId;",
                new { Status = (int)newPedidoStatus, MotoboyId = motoboyId, StopStatus = stopStatus, PedidoId = pedidoId },
                transaction);

            return await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
        }

        private static Task ReturnPedidoToPendingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int pedidoId) =>
            connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Pendente, motoboy_responsavel = NULL, horario_saida = NULL WHERE id = @PedidoId;",
                new { Pendente = (int)StatusPedido.Pendente, PedidoId = pedidoId }, transaction);

        // =====================================================================
        // Travas, leituras e numeracao
        // =====================================================================

        /// <summary>
        /// Garante e trava o cabecalho da(s) fila(s) em ordem crescente de motoboy_id.
        /// Devolve a versao da fila do primeiro motoboy informado.
        /// </summary>
        private static async Task<long> LockQueuesAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, int? otherMotoboyId = null)
        {
            var ids = new List<int> { motoboyId };
            if (otherMotoboyId.HasValue && otherMotoboyId.Value > 0 && otherMotoboyId.Value != motoboyId)
            {
                ids.Add(otherMotoboyId.Value);
            }

            long primaryVersion = 0;
            foreach (var id in ids.OrderBy(x => x))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO delivery_motoboy_route (motoboy_id, estabelecimento_id, version, updated_at_utc) " +
                    "VALUES (@MotoboyId, @EstabelecimentoId, 0, NOW()) ON CONFLICT (motoboy_id, estabelecimento_id) DO NOTHING;",
                    new { MotoboyId = id, EstabelecimentoId = estabelecimentoId }, transaction);
                var version = await connection.ExecuteScalarAsync<long>(
                    "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId FOR UPDATE;",
                    new { MotoboyId = id, EstabelecimentoId = estabelecimentoId }, transaction);
                if (id == motoboyId) primaryVersion = version;
            }
            return primaryVersion;
        }

        private static async Task<long> GetVersionAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId) =>
            await connection.ExecuteScalarAsync<long?>(
                "SELECT version FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction) ?? 0;

        private static async Task<long> BumpRouteVersionAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId) =>
            await connection.ExecuteScalarAsync<long>(
                "UPDATE delivery_motoboy_route SET version = version + 1, updated_at_utc = NOW() " +
                "WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId RETURNING version;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);

        private static Task<PedidoRow?> GetPedidoAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int pedidoId, bool forUpdate) =>
            connection.QuerySingleOrDefaultAsync<PedidoRow?>(
                "SELECT id AS Id, status_pedido AS StatusPedido, motoboy_responsavel AS MotoboyResponsavel " +
                "FROM pedido WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId" + (forUpdate ? " FOR UPDATE;" : ";"),
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);

        private static Task<StopRow?> GetActiveStopForPedidoAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int pedidoId, bool forUpdate) =>
            connection.QuerySingleOrDefaultAsync<StopRow?>(
                $"SELECT {StopColumns} FROM delivery_route_stops s " +
                $"WHERE s.pedido_id = @PedidoId AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status IN {ActiveStatusesSql}" +
                (forUpdate ? " FOR UPDATE;" : ";"),
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);

        private static Task<StopRow?> GetCurrentStopAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, bool forUpdate) =>
            connection.QuerySingleOrDefaultAsync<StopRow?>(
                $"SELECT {StopColumns} FROM delivery_route_stops s " +
                "WHERE s.motoboy_id = @MotoboyId AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status = 'en_route'" +
                (forUpdate ? " FOR UPDATE;" : ";"),
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);

        private static async Task<List<StopRow>> GetActiveStopsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, bool forUpdate) =>
            (await connection.QueryAsync<StopRow>(
                $"SELECT {StopColumns} FROM delivery_route_stops s " +
                $"WHERE s.motoboy_id = @MotoboyId AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status IN {ActiveStatusesSql} " +
                "ORDER BY s.position" + (forUpdate ? " FOR UPDATE;" : ";"),
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction)).ToList();

        /// <summary>Uma entrega em rota por motoboy vale entre estabelecimentos (e o indice unico).</summary>
        private static Task<bool> HasEnRouteAnywhereAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId) =>
            connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND stop_status = 'en_route');",
                new { MotoboyId = motoboyId }, transaction);

        /// <summary>
        /// Renumera as paradas ativas (entrega atual primeiro, depois a ordem atual).
        /// Em duas fases: o indice unico de posicao e checado linha a linha, entao
        /// atribuir 1..n direto colidiria com posicoes que ainda nao foram movidas.
        /// </summary>
        private static async Task RenumberActiveStopsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            await ShiftPositionsOutOfRangeAsync(connection, transaction, estabelecimentoId, motoboyId);
            await connection.ExecuteAsync($@"
UPDATE delivery_route_stops s
   SET position = o.rn, updated_at_utc = NOW()
  FROM (
        SELECT id, ROW_NUMBER() OVER (ORDER BY (stop_status <> 'en_route'), position, id) AS rn
          FROM delivery_route_stops
         WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN {ActiveStatusesSql}
       ) o
 WHERE s.id = o.id;", new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
        }

        /// <summary>Aplica uma ordem explicita (reordenacao), tambem em duas fases.</summary>
        private static async Task ApplyPositionsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            Guid estabelecimentoId, int motoboyId, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            await ShiftPositionsOutOfRangeAsync(connection, transaction, estabelecimentoId, motoboyId);
            await connection.ExecuteAsync($@"
UPDATE delivery_route_stops s
   SET position = v.pos::int, updated_at_utc = NOW()
  FROM UNNEST(@PedidoIds::int[]) WITH ORDINALITY AS v(pedido_id, pos)
 WHERE s.pedido_id = v.pedido_id
   AND s.motoboy_id = @MotoboyId
   AND s.estabelecimento_id = @EstabelecimentoId
   AND s.stop_status IN {ActiveStatusesSql};",
                new { PedidoIds = pedidoIdsOrdenados.ToArray(), MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId },
                transaction);
        }

        private static Task ShiftPositionsOutOfRangeAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId) =>
            connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET position = position + 100000 " +
                $"WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN {ActiveStatusesSql};",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);

        /// <summary>
        /// Promove a proxima parada 'assigned' a entrega atual. Nao promove se o motoboy
        /// ja tem uma entrega em rota (inclusive em outro estabelecimento).
        /// </summary>
        private static async Task TryPromoteNextAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            if (await HasEnRouteAnywhereAsync(connection, transaction, motoboyId)) return;

            var next = await connection.QuerySingleOrDefaultAsync<StopRow?>(
                $"SELECT {StopColumns} FROM delivery_route_stops s " +
                "WHERE s.motoboy_id = @MotoboyId AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status = 'assigned' " +
                "ORDER BY s.position LIMIT 1 FOR UPDATE;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            if (next == null) return;

            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'en_route', started_at_utc = NOW(), updated_at_utc = NOW() WHERE id = @Id;",
                new { next.Id }, transaction);
            var saidaNow = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "horario_saida");
            await connection.ExecuteAsync(
                $"UPDATE pedido SET status_pedido = @EmRota, horario_saida = {saidaNow} WHERE id = @PedidoId;",
                new { EmRota = (int)StatusPedido.EmRota, next.PedidoId }, transaction);
        }

        // =====================================================================
        // Elegibilidade
        // =====================================================================

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

        private static async Task<EligibilityRow> EnsureEligibleAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, Guid estabelecimentoId)
        {
            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (!eligibility.HasLink)
            {
                throw new DeliveryDomainException(403, "LINK_FORBIDDEN", "Motoboy sem vinculo ativo com o estabelecimento.");
            }
            if (!eligibility.SessionId.HasValue)
            {
                throw new DeliveryDomainException(409, "MOTOBOY_NOT_ELIGIBLE", "Motoboy nao esta online neste estabelecimento.");
            }
            return eligibility;
        }

        private static async Task<Guid?> GetSessionIdAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, int motoboyId, Guid estabelecimentoId) =>
            (await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId)).SessionId;

        // =====================================================================
        // Snapshot da fila
        // =====================================================================

        private sealed class StopDetailRow
        {
            public long Id { get; set; }
            public int PedidoId { get; set; }
            public int Position { get; set; }
            public string StopStatus { get; set; } = "assigned";
            public DateTimeOffset AssignedAtUtc { get; set; }
            public DateTimeOffset? PickedUpAtUtc { get; set; }
            public DateTimeOffset? ArrivedAtUtc { get; set; }
            public string? NomeCliente { get; set; }
            public string? TelefoneCliente { get; set; }
            public string? EnderecoEntrega { get; set; }
            public string? Rua { get; set; }
            public string? Numero { get; set; }
            public string? Bairro { get; set; }
            public string? Cidade { get; set; }
            public string? Estado { get; set; }
            public string? Cep { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Items { get; set; }
            public decimal? Value { get; set; }
            public string? TipoPagamento { get; set; }
            public string? StatusPagamento { get; set; }
            public decimal? Troco { get; set; }
            public string? Observacoes { get; set; }
            public string? PrevisaoEntregaRaw { get; set; }
            public string? DataPedidoRaw { get; set; }
            public bool HasDeliveryCode { get; set; }
            public bool Locked { get; set; }
        }

        private static async Task<MotoboyQueueDto> BuildSnapshotAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, long version)
        {
            var rules = await HasRouteRulesSchemaAsync(connection, transaction);
            // Colunas legadas podem ser text/numeric/time: le como texto e converte com
            // seguranca, para um cadastro mal preenchido nao derrubar a fila inteira.
            var rows = (await connection.QueryAsync<StopDetailRow>($@"
SELECT s.id AS Id, s.pedido_id AS PedidoId, s.position AS Position, s.stop_status AS StopStatus,
       s.assigned_at_utc AS AssignedAtUtc, s.picked_up_at_utc AS PickedUpAtUtc, s.arrived_at_utc AS ArrivedAtUtc,
       p.nome_cliente::text AS NomeCliente,
       p.telefone_cliente::text AS TelefoneCliente,
       p.endereco_entrega::text AS EnderecoEntrega,
       p.entrega_rua::text AS Rua,
       p.entrega_numero::text AS Numero,
       p.entrega_bairro::text AS Bairro,
       p.entrega_cidade::text AS Cidade,
       p.entrega_estado::text AS Estado,
       p.entrega_cep::text AS Cep,
       CASE WHEN p.latitude::text ~ {NumericPattern} THEN p.latitude::text::DOUBLE PRECISION END AS Latitude,
       CASE WHEN p.longitude::text ~ {NumericPattern} THEN p.longitude::text::DOUBLE PRECISION END AS Longitude,
       p.items::text AS Items,
       CASE WHEN p.value::text ~ {NumericPattern} THEN p.value::text::NUMERIC END AS Value,
       p.tipo_pagamento::text AS TipoPagamento,
       p.status_pagamento::text AS StatusPagamento,
       CASE WHEN p.troco::text ~ {NumericPattern} THEN p.troco::text::NUMERIC END AS Troco,
       p.observacoes::text AS Observacoes,
       p.previsao_entrega::text AS PrevisaoEntregaRaw,
       p.data_pedido::text AS DataPedidoRaw,
       (NULLIF(BTRIM(COALESCE(p.codigo_entrega::text, '')), '') IS NOT NULL) AS HasDeliveryCode,
       {(rules ? "s.locked" : "FALSE")} AS Locked
  FROM delivery_route_stops s
  JOIN pedido p ON p.id = s.pedido_id
 WHERE s.motoboy_id = @MotoboyId AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status IN {ActiveStatusesSql}
 ORDER BY s.position;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction)).ToList();

            var settings = await GetSettingsInternalAsync(connection, transaction, estabelecimentoId);

            RouteStopDto Map(StopDetailRow row) => new()
            {
                PedidoId = row.PedidoId,
                Position = row.Position,
                Status = row.StopStatus,
                AssignedAtUtc = row.AssignedAtUtc,
                PickedUpAtUtc = row.PickedUpAtUtc,
                ArrivedAtUtc = row.ArrivedAtUtc,
                Locked = row.Locked,
                Pedido = new DeliveryStopOrderDto
                {
                    Id = row.PedidoId,
                    NomeCliente = row.NomeCliente,
                    TelefoneCliente = row.TelefoneCliente,
                    EnderecoEntrega = row.EnderecoEntrega,
                    Rua = row.Rua,
                    Numero = row.Numero,
                    Bairro = row.Bairro,
                    Cidade = row.Cidade,
                    Estado = row.Estado,
                    Cep = row.Cep,
                    Latitude = row.Latitude,
                    Longitude = row.Longitude,
                    Items = row.Items,
                    Value = row.Value,
                    TipoPagamento = row.TipoPagamento,
                    StatusPagamento = row.StatusPagamento,
                    Troco = row.Troco,
                    Observacoes = row.Observacoes,
                    PrevisaoEntrega = DeliveryRules.ParseStoredDateTime(
                        row.PrevisaoEntregaRaw, DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw)),
                    RequerCodigoEntrega = settings.RequireDeliveryCode && row.HasDeliveryCode
                }
            };

            var current = rows.FirstOrDefault(r => r.StopStatus == "en_route");
            var routeState = await ReadRouteStateAsync(connection, transaction, estabelecimentoId, motoboyId, rules);
            return new MotoboyQueueDto
            {
                RouteState = routeState.State,
                ReturningSinceUtc = routeState.Since,
                MotoboyId = motoboyId,
                EstabelecimentoId = estabelecimentoId,
                Version = version,
                Current = current == null ? null : Map(current),
                Next = rows.Where(r => r.StopStatus == "assigned").Select(Map).ToList(),
                Politicas = settings.ToPolicies()
            };
        }

        private static MotoboyQueueDto EmptyQueue(Guid estabelecimentoId) =>
            new() { MotoboyId = 0, EstabelecimentoId = estabelecimentoId, Version = 0 };

        // =====================================================================
        // Eventos de tempo real (outbox)
        // =====================================================================

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
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                motoboyId,
                pedidoId,
                action,
                version,
                occurredAtUtc = DateTimeOffset.UtcNow
            });

            await InsertOutboxAsync(connection, transaction, DeliveryRealtimeEvents.DeliveryQueueUpdated,
                estabelecimentoId, motoboyId, version, payload, sessionId.HasValue ? new[] { sessionId.Value } : Array.Empty<Guid>());
        }

        private static async Task InsertOutboxAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string eventName,
            Guid estabelecimentoId,
            int? motoboyId,
            long aggregateVersion,
            string payload,
            IEnumerable<Guid> sessionIds)
        {
            var targets = new List<string> { DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId) };
            targets.AddRange(sessionIds.Distinct().Select(DeliveryRealtimeEvents.SessionGroup));

            foreach (var target in targets)
            {
                await connection.ExecuteAsync(@"
INSERT INTO delivery_realtime_outbox (
    event_id, event_name, target_group, estabelecimento_id, motoboy_id,
    session_id, session_epoch, aggregate_version, payload, occurred_at_utc)
VALUES (
    @EventId, @EventName, @TargetGroup, @EstabelecimentoId, @MotoboyId,
    NULL, NULL, @AggregateVersion, @Payload::jsonb, NOW());",
                    new
                    {
                        EventId = Guid.NewGuid(),
                        EventName = eventName,
                        TargetGroup = target,
                        EstabelecimentoId = estabelecimentoId,
                        MotoboyId = motoboyId,
                        AggregateVersion = aggregateVersion,
                        Payload = payload
                    }, transaction);
            }
        }
    }
}
