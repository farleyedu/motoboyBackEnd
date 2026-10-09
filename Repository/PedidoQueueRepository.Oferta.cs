using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Confirmacao do motoboy: a rota enviada pelo atendente vira uma OFERTA (as paradas ficam
    /// 'assigned' com offered_at_utc preenchido e o pedido em AguardandoMotoboy) ate o motoboy
    /// aceitar a rota, recusar a rota inteira ou recusar um pedido. Sem resposta no prazo, a
    /// oferta e recusada sozinha. Enquanto a oferta esta pendente o motoboy fica ocupado.
    /// </summary>
    public sealed partial class PedidoQueueRepository
    {
        private static DeliveryDomainException OfertaPending() =>
            new(503, "MIGRATION_PENDING", "A confirmacao do motoboy ainda nao foi habilitada neste ambiente (migration 20261002_02 pendente).");

        private sealed class OfferSettingsRow
        {
            public bool Required { get; set; }
            public int Minutes { get; set; } = OfertaRotaRules.PrazoPadraoMinutos;
        }

        private sealed class OfferStopRow
        {
            public long Id { get; set; }
            public int PedidoId { get; set; }
        }

        // ---- parametros -----------------------------------------------------------------

        private static async Task<OfferSettingsRow> ReadOfferSettingsAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid estabelecimentoId, CancellationToken cancellationToken = default)
        {
            if (!await PedidoColumnTypes.HasOfertaSchemaAsync(connection, transaction, cancellationToken)) return new OfferSettingsRow();
            var row = await connection.QuerySingleOrDefaultAsync<OfferSettingsRow?>(new CommandDefinition(@"
SELECT require_motoboy_acceptance AS Required, offer_timeout_minutes AS Minutes
  FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;",
                new { EstabelecimentoId = estabelecimentoId }, transaction, commandTimeout: 10, cancellationToken: cancellationToken));
            return row ?? new OfferSettingsRow();
        }

        /// <summary>Id novo de oferta quando o estabelecimento exige o aceite; null = atribuicao direta (como sempre).</summary>
        private static async Task<Guid?> NewOfferIdIfRequiredAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId)
        {
            var settings = await ReadOfferSettingsAsync(connection, transaction, estabelecimentoId);
            return settings.Required ? Guid.NewGuid() : null;
        }

        private static async Task ApplyOfferSettingsAsync(DeliverySettingsDto settings, NpgsqlConnection connection, Guid estabelecimentoId)
        {
            var row = await ReadOfferSettingsAsync(connection, null, estabelecimentoId);
            settings.RequireMotoboyAcceptance = row.Required;
            settings.OfferTimeoutMinutes = row.Minutes;
        }

        private static async Task SaveOfferSettingsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, UpdateDeliverySettingsRequest request)
        {
            if (request.RequireMotoboyAcceptance == null && request.OfferTimeoutMinutes == null) return;
            if (!await PedidoColumnTypes.HasOfertaSchemaAsync(connection, transaction))
            {
                // O painel envia os valores sempre; antes da migration, os padroes nao podem travar o salvar das demais configuracoes.
                if (!(request.RequireMotoboyAcceptance ?? false) && (request.OfferTimeoutMinutes ?? OfertaRotaRules.PrazoPadraoMinutos) == OfertaRotaRules.PrazoPadraoMinutos) return;
                throw OfertaPending();
            }
            await connection.ExecuteAsync(@"
UPDATE delivery_settings
   SET require_motoboy_acceptance = COALESCE(@Required, require_motoboy_acceptance),
       offer_timeout_minutes = COALESCE(@Minutes, offer_timeout_minutes)
 WHERE estabelecimento_id = @EstabelecimentoId;",
                new { Required = request.RequireMotoboyAcceptance, Minutes = request.OfferTimeoutMinutes, EstabelecimentoId = estabelecimentoId },
                transaction);
        }

        // ---- ocupado ---------------------------------------------------------------------

        /// <summary>O motoboy fica ocupado enquanto nao responde a uma rota: nao recebe outra (a mesma oferta pode crescer em lote).</summary>
        private static async Task EnsureNoOtherPendingOfferAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, Guid? offerId)
        {
            if (!await PedidoColumnTypes.HasOfertaSchemaAsync(connection, transaction)) return;
            var busy = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM delivery_route_stops
     WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId
       AND stop_status = 'assigned' AND offered_at_utc IS NOT NULL
       AND offer_id IS DISTINCT FROM @OfferId);",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId, OfferId = offerId }, transaction);
            if (busy)
            {
                throw new DeliveryDomainException(409, "MOTOBOY_HAS_PENDING_OFFER",
                    "O motoboy ainda nao respondeu a uma rota enviada antes. Espere ele aceitar ou recusar.");
            }
        }

        // ---- atribuicao em lote (rota) -----------------------------------------------------

        /// <summary>
        /// Envia a rota inteira ao motoboy numa transacao so: ou todos os pedidos entram ou nenhum
        /// (antes eram uma chamada por pedido, e uma falha no meio deixava a rota pela metade).
        /// </summary>
        public async Task<MotoboyQueueDto> AssignRouteAsync(
            Guid estabelecimentoId, int actorUserId, int motoboyId, IReadOnlyList<int> pedidoIdsOrdenados)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var offerId = await NewOfferIdIfRequiredAsync(connection, transaction, estabelecimentoId);
            foreach (var pedidoId in pedidoIdsOrdenados)
            {
                try
                {
                    await AssignCoreAsync(connection, transaction, estabelecimentoId, actorUserId, motoboyId, pedidoId, offerId);
                }
                catch (DeliveryDomainException ex)
                {
                    // Diz qual pedido barrou a rota; nada foi gravado (a transacao nao confirma).
                    throw new DeliveryDomainException(ex.StatusCode, ex.Code, $"Pedido {pedidoId}: {ex.Message}");
                }
            }

            var version = await GetVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        // ---- aceitar / recusar a rota ------------------------------------------------------

        private static async Task<List<OfferStopRow>> ListOfferedStopsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId) =>
            (await connection.QueryAsync<OfferStopRow>(@"
SELECT s.id AS Id, s.pedido_id AS PedidoId
  FROM delivery_route_stops s
 WHERE s.motoboy_id = @MotoboyId AND s.estabelecimento_id = @EstabelecimentoId
   AND s.stop_status = 'assigned' AND s.offered_at_utc IS NOT NULL
 ORDER BY s.position
 FOR UPDATE;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction)).ToList();

        /// <summary>Aceita a rota: as paradas entram na fila e a primeira vira a entrega atual (se ele nao tem outra).</summary>
        public Task<MotoboyQueueDto> AcceptOfferAsync(Guid estabelecimentoId, int motoboyId) => AcceptOfferCoreAsync(estabelecimentoId, motoboyId, null);
        public Task<MotoboyQueueDto> AcceptOfferForAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId) => AcceptOfferCoreAsync(estabelecimentoId, motoboyId, expectedOfferId);
        public Task<MotoboyQueueDto> AcceptPricedOfferAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId, long expectedVersion, IReadOnlyList<int>? acceptedPedidoIds = null) => AcceptOfferCoreAsync(estabelecimentoId, motoboyId, expectedOfferId, expectedVersion, acceptedPedidoIds);
        private async Task<MotoboyQueueDto> AcceptOfferCoreAsync(Guid estabelecimentoId, int motoboyId, Guid? expectedOfferId, long? expectedVersion = null, IReadOnlyList<int>? acceptedPedidoIds = null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasOfertaSchemaAsync(connection, null)) throw OfertaPending();
            await using var transaction = await connection.BeginTransactionAsync();

            var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var stops = await ListOfferedStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var before = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, currentVersion);
            if (before.Offer?.Stops.Any(s => s.Earnings != null) == true && expectedVersion != currentVersion)
                throw new DeliveryDomainException(409, "OFFER_EARNINGS_CHANGED", "Confira novamente os pedidos e os ganhos da oferta antes de aceitar.");
            if (stops.Count > 0 && expectedOfferId.HasValue && before.Offer?.OfferId != expectedOfferId)
                throw new DeliveryDomainException(409, "OFFER_CHANGED", "A oferta mudou. Confira a nova rota antes de aceitar.");
            if (before.Offer?.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new DeliveryDomainException(410, "OFFER_EXPIRED", "O prazo desta oferta terminou.");
            if (stops.Count == 0)
            {
                // Idempotente: sem oferta pendente (ja aceita, recusada ou expirada) devolve a fila como esta.
                var same = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, currentVersion);
                await transaction.CommitAsync();
                return same;
            }

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (acceptedPedidoIds != null)
            {
                if (expectedVersion != currentVersion || acceptedPedidoIds.Count is < 1 or > 100 || acceptedPedidoIds.Distinct().Count() != acceptedPedidoIds.Count || acceptedPedidoIds.Any(id => !stops.Any(s => s.PedidoId == id)))
                    throw new DeliveryDomainException(409, "OFFER_SELECTION_CHANGED", "Confira os pedidos selecionados e os ganhos da oferta novamente.");
                foreach (var declined in stops.Where(s => !acceptedPedidoIds.Contains(s.PedidoId)))
                {
                    await connection.ExecuteAsync("UPDATE delivery_route_stops SET stop_status='refused',refused_at_utc=NOW(),refusal_reason='Não selecionado pelo motoboy no aceite',updated_at_utc=NOW() WHERE id=@Id;", new { declined.Id }, transaction);
                    await ReturnPedidoToPendingAsync(connection, transaction, declined.PedidoId);
                    await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, declined.PedidoId, "Pedido recusado no aceite da oferta.");
                    await PublishOrderEventAsync(connection, transaction, estabelecimentoId, 0, declined.PedidoId, "offer_rejected");
                }
                stops = stops.Where(s => acceptedPedidoIds.Contains(s.PedidoId)).ToList();
            }
            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET offered_at_utc = NULL, updated_at_utc = NOW() WHERE id = ANY(@Ids);",
                new { Ids = stops.Select(s => s.Id).ToArray() }, transaction);
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Atribuido WHERE id = ANY(@PedidoIds);",
                new { Atribuido = (int)StatusPedido.Atribuido, PedidoIds = stops.Select(s => s.PedidoId).ToArray() }, transaction);

            await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, stops[0].PedidoId, "offer_accepted", version, eligibility.SessionId);
            foreach (var stop in stops)
            {
                await PublishOrderEventAsync(connection, transaction, estabelecimentoId, 0, stop.PedidoId, "offer_accepted");
            }

            var snapshot = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>Recusa a rota inteira: todos os pedidos oferecidos voltam a Pendente, com o motivo.</summary>
        public Task<MotoboyQueueDto> RejectOfferAsync(Guid estabelecimentoId, int motoboyId, string? motivo) => RejectExpectedOfferCoreAsync(estabelecimentoId, motoboyId, motivo, null);
        public Task<MotoboyQueueDto> RejectOfferForAsync(Guid estabelecimentoId, int motoboyId, Guid expectedOfferId, string? motivo) => RejectExpectedOfferCoreAsync(estabelecimentoId, motoboyId, motivo, expectedOfferId);
        private async Task<MotoboyQueueDto> RejectExpectedOfferCoreAsync(Guid estabelecimentoId, int motoboyId, string? motivo, Guid? expectedOfferId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasOfertaSchemaAsync(connection, null)) throw OfertaPending();
            await using var transaction = await connection.BeginTransactionAsync();

            // Uma oferta pendente sempre pode ser recusada. A regra da loja continua
            // aplicável à devolução de pedidos que o motoboy já aceitou.
            if (expectedOfferId.HasValue)
            {
                var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
                var before = await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, currentVersion);
                if (before.Offer != null && before.Offer.OfferId != expectedOfferId)
                    throw new DeliveryDomainException(409, "OFFER_CHANGED", "A oferta mudou. Confira a nova rota antes de recusar.");
            }

            var snapshot = await RejectOfferCoreAsync(connection, transaction, estabelecimentoId, motoboyId, motivo, "offer_rejected");
            await transaction.CommitAsync();
            return snapshot;
        }

        private async Task<MotoboyQueueDto> RejectOfferCoreAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, string? motivo, string action)
        {
            var currentVersion = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var stops = await ListOfferedStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            if (stops.Count == 0)
            {
                return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, currentVersion);
            }

            foreach (var stop in stops)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'refused', refused_at_utc = NOW(), refusal_reason = @Motivo, updated_at_utc = NOW() WHERE id = @Id;",
                    new { stop.Id, Motivo = motivo }, transaction);
                await ReturnPedidoToPendingAsync(connection, transaction, stop.PedidoId);
                await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, stop.PedidoId, "Rota recusada pelo motoboy.");
                await PublishOrderEventAsync(connection, transaction, estabelecimentoId, 0, stop.PedidoId, action);
            }

            return await FinishStopRemovalAsync(connection, transaction, estabelecimentoId, motoboyId,
                stops[0].PedidoId, action, promoteNext: false);
        }

        // ---- sem resposta -----------------------------------------------------------------

        private sealed class ExpiredOfferRow
        {
            public Guid EstabelecimentoId { get; set; }
            public int MotoboyId { get; set; }
        }

        /// <summary>Recusa sozinha as ofertas que passaram do prazo sem resposta. Devolve quantos motoboys foram tratados.</summary>
        public async Task<int> ExpireOffersAsync(DateTimeOffset agoraUtc)
        {
            List<ExpiredOfferRow> expired;
            await using (var peek = await _dataSource.OpenConnectionAsync())
            {
                if (!await PedidoColumnTypes.HasOfertaSchemaAsync(peek, null)) return 0;
                expired = (await peek.QueryAsync<ExpiredOfferRow>(@"
SELECT DISTINCT s.estabelecimento_id AS EstabelecimentoId, s.motoboy_id AS MotoboyId
  FROM delivery_route_stops s
  LEFT JOIN delivery_settings ds ON ds.estabelecimento_id = s.estabelecimento_id
 WHERE s.stop_status = 'assigned' AND s.offered_at_utc IS NOT NULL
   AND s.offered_at_utc <= @Now - make_interval(mins => COALESCE(ds.offer_timeout_minutes, @DefaultMinutes));",
                    new { Now = agoraUtc, DefaultMinutes = OfertaRotaRules.PrazoPadraoMinutos })).ToList();
            }

            var handled = 0;
            foreach (var item in expired)
            {
                try
                {
                    await using var connection = await _dataSource.OpenConnectionAsync();
                    await using var transaction = await connection.BeginTransactionAsync();
                    await RejectOfferCoreAsync(connection, transaction, item.EstabelecimentoId, item.MotoboyId,
                        OfertaRotaRules.MotivoSemResposta, "offer_expired");
                    await transaction.CommitAsync();
                    handled++;
                }
                catch (Exception)
                {
                    // A proxima passada tenta de novo; um motoboy com problema nao trava os demais.
                }
            }
            return handled;
        }
    }
}
