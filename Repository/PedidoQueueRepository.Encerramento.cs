using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Model.Enum;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Encerramento automatico de pedidos em aberto (status EncerradoAuto) e reabertura.
    /// Mesmo molde do cancelamento: trava a fila do motoboy, tira a parada, renumera e avisa o
    /// painel e o motoboy. A diferenca e que grava em pedido_encerramento com qual motoboy o
    /// pedido estava, para no futuro contar como "nao entregue" na conta dele.
    /// </summary>
    public sealed partial class PedidoQueueRepository
    {
        private static DeliveryDomainException EncerramentoPending() =>
            new(503, "MIGRATION_PENDING", "O encerramento automatico ainda nao foi habilitado neste ambiente (migration 20261002_01 pendente).");

        private sealed class EncerramentoOpenRow
        {
            public int Id { get; set; }
            public string? DataPedidoRaw { get; set; }
            public string? HorarioPedidoRaw { get; set; }
        }

        private static async Task ApplyEncerramentoSettingsAsync(DeliverySettingsDto settings, NpgsqlConnection connection, Guid estabelecimentoId)
        {
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, null)) return;
            var row = await connection.QuerySingleOrDefaultAsync<EncerramentoSettings?>(@"
SELECT encerramento_auto_ativo AS Ativo, encerramento_auto_horas AS Horas
  FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;", new { EstabelecimentoId = estabelecimentoId });
            if (row == null) return;
            settings.EncerramentoAutoAtivo = row.Ativo;
            settings.EncerramentoAutoHoras = row.Horas;
        }

        private static async Task SaveEncerramentoSettingsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, UpdateDeliverySettingsRequest request)
        {
            if (request.EncerramentoAutoAtivo == null && request.EncerramentoAutoHoras == null) return;
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, transaction))
            {
                // O painel envia os valores sempre; antes da migration, os padroes nao podem travar o salvar das demais configuracoes.
                if ((request.EncerramentoAutoAtivo ?? true) && (request.EncerramentoAutoHoras ?? EncerramentoRules.HorasPadrao) == EncerramentoRules.HorasPadrao) return;
                throw EncerramentoPending();
            }
            await connection.ExecuteAsync(@"
UPDATE delivery_settings
   SET encerramento_auto_ativo = COALESCE(@Ativo, encerramento_auto_ativo),
       encerramento_auto_horas = COALESCE(@Horas, encerramento_auto_horas)
 WHERE estabelecimento_id = @EstabelecimentoId;",
                new { Ativo = request.EncerramentoAutoAtivo, Horas = request.EncerramentoAutoHoras, EstabelecimentoId = estabelecimentoId },
                transaction);
        }

        /// <summary>Sem a migration nada e encerrado sozinho (o padrao seria ligado, mas nao ha onde gravar o historico).</summary>
        public async Task<EncerramentoSettings> GetEncerramentoSettingsAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, null))
            {
                return new EncerramentoSettings { Ativo = false };
            }

            var row = await connection.QuerySingleOrDefaultAsync<EncerramentoSettings?>(@"
SELECT encerramento_auto_ativo AS Ativo, encerramento_auto_horas AS Horas
  FROM delivery_settings
 WHERE estabelecimento_id = @EstabelecimentoId;", new { EstabelecimentoId = estabelecimentoId });
            // Sem linha em delivery_settings valem os padroes (ligado, 4 h).
            return row ?? new EncerramentoSettings();
        }

        public async Task<IReadOnlyList<Guid>> ListEstablishmentsWithOpenOrdersAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, null))
            {
                return Array.Empty<Guid>();
            }

            var ids = await connection.QueryAsync<Guid>(
                "SELECT DISTINCT id_estabelecimento FROM pedido " +
                "WHERE COALESCE(status_pedido, 1) IN (1, 2, 5, 8) AND id_estabelecimento IS NOT NULL;");
            return ids.ToList();
        }

        /// <summary>Pedidos em aberto do estabelecimento com o instante em que foram feitos (null = sem horario confiavel).</summary>
        public async Task<IReadOnlyList<EncerramentoCandidate>> ListOpenOrdersForEncerramentoAsync(Guid estabelecimentoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<EncerramentoOpenRow>(@"
SELECT p.id AS Id, p.data_pedido::text AS DataPedidoRaw, p.horario_pedido::text AS HorarioPedidoRaw
  FROM pedido p
 WHERE p.id_estabelecimento = @EstabelecimentoId
   AND COALESCE(p.status_pedido, 1) IN (1, 2, 5, 8);", new { EstabelecimentoId = estabelecimentoId });

            return rows.Select(row =>
            {
                var data = DeliveryRules.ParseStoredDateTime(row.DataPedidoRaw);
                var horario = DeliveryRules.ParseStoredDateTime(row.HorarioPedidoRaw, data);
                return new EncerramentoCandidate { Id = row.Id, FeitoEmUtc = OrderWindowRules.PlacedAtUtc(horario ?? data) };
            }).ToList();
        }

        /// <summary>
        /// Encerra um pedido em aberto. Devolve false quando ele nao esta mais em aberto (ja entregue,
        /// cancelado, encerrado...), o que torna o comando seguro de repetir.
        /// </summary>
        public async Task<bool> EncerrarPedidoAsync(Guid estabelecimentoId, int pedidoId, string motivo, int? actorUserId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, null)) throw EncerramentoPending();
            await using var transaction = await connection.BeginTransactionAsync();

            var preview = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false);
            if (preview == null) return false;
            var previewStop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: false);
            var motoboyId = previewStop?.MotoboyId ?? preview.MotoboyResponsavel ?? 0;
            if (motoboyId > 0)
            {
                await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            }

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            var status = StatusPedidoExtensions.FromDbValue(pedido?.StatusPedido) ?? StatusPedido.Pendente;
            if (pedido == null || !EncerramentoRules.IsOpen(status))
            {
                await transaction.CommitAsync();
                return false;
            }

            var stop = await GetActiveStopForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true);
            if (stop != null && stop.MotoboyId != motoboyId)
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila do pedido mudou. Tente novamente.");
            }

            var wasEnRoute = stop?.StopStatus == "en_route";
            if (stop != null)
            {
                await connection.ExecuteAsync(
                    "UPDATE delivery_route_stops SET stop_status = 'canceled', canceled_at_utc = NOW(), cancel_reason = @Motivo, updated_at_utc = NOW() WHERE id = @Id;",
                    new { stop.Id, Motivo = EncerramentoRules.DescricaoMotivo(motivo) }, transaction);
            }

            // motoboy_responsavel fica como esta: o painel continua mostrando com quem o pedido estava.
            await connection.ExecuteAsync(
                "UPDATE pedido SET status_pedido = @Encerrado WHERE id = @PedidoId;",
                new { Encerrado = (int)StatusPedido.EncerradoAuto, PedidoId = pedidoId }, transaction);
            // O pedido esta em aberto: qualquer encerramento ainda "valendo" e resto de um caminho antigo.
            await connection.ExecuteAsync(
                "UPDATE pedido_encerramento SET reaberto_em = NOW() WHERE pedido_id = @PedidoId AND reaberto_em IS NULL;",
                new { PedidoId = pedidoId }, transaction);
            await connection.ExecuteAsync(@"
INSERT INTO pedido_encerramento (estabelecimento_id, pedido_id, motoboy_id, status_anterior, motivo)
VALUES (@EstabelecimentoId, @PedidoId, @MotoboyId, @StatusAnterior, @Motivo);",
                new
                {
                    EstabelecimentoId = estabelecimentoId,
                    PedidoId = pedidoId,
                    MotoboyId = motoboyId > 0 ? motoboyId : (int?)null,
                    StatusAnterior = (int)status,
                    Motivo = motivo
                }, transaction);
            await CancelPendingTransfersForPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, "Pedido encerrado.");

            if (stop == null || motoboyId <= 0)
            {
                await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId ?? 0, pedidoId, "auto_closed");
                await transaction.CommitAsync();
                return true;
            }

            var eligibility = await GetEligibilityAsync(connection, transaction, motoboyId, estabelecimentoId);
            if (wasEnRoute && eligibility.SessionId.HasValue)
            {
                await TryPromoteNextAsync(connection, transaction, estabelecimentoId, motoboyId);
            }
            await RenumberActiveStopsAsync(connection, transaction, estabelecimentoId, motoboyId);
            var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, pedidoId, "auto_closed", version, eligibility.SessionId);
            await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId ?? 0, pedidoId, "auto_closed");

            await transaction.CommitAsync();
            return true;
        }

        /// <summary>
        /// Reabre um pedido encerrado automaticamente: volta a Pendente, sem motoboy e com prazo novo
        /// (o antigo ja venceu). O historico de encerramento fica, com a data da reabertura.
        /// </summary>
        public async Task<CreatedPedidoDto> ReabrirEncerradoAsync(Guid estabelecimentoId, int actorUserId, int pedidoId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            if (!await PedidoColumnTypes.HasEncerramentoSchemaAsync(connection, null)) throw EncerramentoPending();
            // Lido antes da transacao: se a coluna nao existir, o erro nao aborta a transacao do comando.
            var previsaoMinutos = await OrderWindowStore.ReadDefaultDeliveryMinutesAsync(connection, estabelecimentoId)
                ?? ManualOrderRules.DefaultPrevisaoMinutos;
            await using var transaction = await connection.BeginTransactionAsync();

            var pedido = await GetPedidoAsync(connection, transaction, estabelecimentoId, pedidoId, forUpdate: true)
                ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
            var status = StatusPedidoExtensions.FromDbValue(pedido.StatusPedido) ?? StatusPedido.Pendente;
            if (!EncerramentoRules.CanReopen(status))
            {
                throw new DeliveryDomainException(409, "ORDER_NOT_REOPENABLE", "So pedido encerrado automaticamente pode ser reaberto.");
            }

            // Encerrado nao tem parada ativa; por seguranca, qualquer resto vira "removed".
            await connection.ExecuteAsync(
                "UPDATE delivery_route_stops SET stop_status = 'removed', removed_at_utc = NOW(), updated_at_utc = NOW() " +
                "WHERE pedido_id = @PedidoId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN ('assigned', 'en_route');",
                new { PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId }, transaction);

            var previsao = await PedidoColumnTypes.LocalNowSqlAsync(connection, transaction, "previsao_entrega", "@PrevisaoMinutos");
            await connection.ExecuteAsync($@"
UPDATE pedido
   SET status_pedido = @Pendente, motoboy_responsavel = NULL, horario_saida = NULL, horario_entrega = NULL,
       previsao_entrega = {previsao}
 WHERE id = @PedidoId AND id_estabelecimento = @EstabelecimentoId;",
                new { Pendente = (int)StatusPedido.Pendente, PedidoId = pedidoId, EstabelecimentoId = estabelecimentoId, PrevisaoMinutos = previsaoMinutos },
                transaction);
            await connection.ExecuteAsync(@"
UPDATE pedido_encerramento
   SET reaberto_em = NOW(), reaberto_por_user_id = @ActorUserId
 WHERE pedido_id = @PedidoId AND reaberto_em IS NULL;", new { ActorUserId = actorUserId, PedidoId = pedidoId }, transaction);

            await PublishOrderEventAsync(connection, transaction, estabelecimentoId, actorUserId, pedidoId, "reopened");
            await transaction.CommitAsync();
            return new CreatedPedidoDto { Id = pedidoId, Status = "pendente" };
        }
    }
}
