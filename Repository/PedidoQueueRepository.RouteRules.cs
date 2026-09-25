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
    /// Fase 4: pedidos travados (ancoras) e retorno a loja. Tudo tolera o banco sem a migration
    /// 20260927_01: sem as colunas nada e travado, a rota nunca entra em retorno e os comandos novos
    /// respondem 503 MIGRATION_PENDING.
    /// </summary>
    public sealed partial class PedidoQueueRepository
    {
        private static Task<bool> HasRouteRulesSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
            PedidoColumnTypes.HasRouteRulesSchemaAsync(connection, transaction);

        private static DeliveryDomainException MigrationPending() =>
            new(503, "MIGRATION_PENDING", "As regras de rota ainda nao foram habilitadas neste ambiente (migration 20260927_01 pendente).");

        // =====================================================================
        // Lock
        // =====================================================================

        private static async Task<HashSet<int>> GetLockedPedidoIdsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) return new HashSet<int>();
            var ids = await connection.QueryAsync<int>(
                $"SELECT pedido_id FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND locked AND stop_status IN {ActiveStatusesSql};",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            return ids.ToHashSet();
        }

        private static async Task<bool> IsStopLockedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long stopId)
        {
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) return false;
            return await connection.ExecuteScalarAsync<bool>("SELECT COALESCE(locked, FALSE) FROM delivery_route_stops WHERE id = @Id;", new { Id = stopId }, transaction);
        }

        public async Task<LockPedidosResultDto> SetLockedAsync(
            Guid estabelecimentoId, int actorUserId, IReadOnlyList<int> pedidoIds, bool locked)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) throw MigrationPending();

            // Leitura sem trava so para descobrir quais filas travar (regra 1 da classe).
            var preview = (await connection.QueryAsync<StopRow>(
                $"SELECT {StopColumns} FROM delivery_route_stops s WHERE s.pedido_id = ANY(@Ids) AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status IN {ActiveStatusesSql};",
                new { Ids = pedidoIds.ToArray(), EstabelecimentoId = estabelecimentoId }, transaction)).ToList();
            var missing = pedidoIds.Except(preview.Select(s => s.PedidoId)).ToList();
            if (missing.Count > 0)
            {
                throw new DeliveryDomainException(404, "PEDIDO_NOT_IN_QUEUE",
                    "Ha pedido que nao esta na fila de nenhum motoboy: so pedidos atribuidos ou em rota podem ser travados.",
                    new { pedidoIds = missing });
            }

            var motoboyIds = preview.Select(s => s.MotoboyId).Distinct().OrderBy(id => id).ToList();
            foreach (var motoboyId in motoboyIds)
            {
                await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            }

            var stops = (await connection.QueryAsync<StopRow>(
                $"SELECT {StopColumns} FROM delivery_route_stops s WHERE s.pedido_id = ANY(@Ids) AND s.estabelecimento_id = @EstabelecimentoId AND s.stop_status IN {ActiveStatusesSql} ORDER BY s.id FOR UPDATE;",
                new { Ids = pedidoIds.ToArray(), EstabelecimentoId = estabelecimentoId }, transaction)).ToList();
            if (stops.Count != preview.Count || stops.Any(s => preview.All(p => p.Id != s.Id || p.MotoboyId != s.MotoboyId)))
            {
                throw new DeliveryDomainException(409, "QUEUE_CHANGED", "A fila mudou durante a operacao. Recarregue e tente novamente.");
            }

            await connection.ExecuteAsync(@"
UPDATE delivery_route_stops
   SET locked = @Locked,
       locked_by_user_id = CASE WHEN @Locked THEN @ActorUserId ELSE NULL END,
       locked_at_utc = CASE WHEN @Locked THEN NOW() ELSE NULL END,
       updated_at_utc = NOW()
 WHERE id = ANY(@StopIds) AND locked IS DISTINCT FROM @Locked;",
                new { Locked = locked, ActorUserId = actorUserId, StopIds = stops.Select(s => s.Id).ToArray() }, transaction);

            var result = new LockPedidosResultDto();
            foreach (var motoboyId in motoboyIds)
            {
                var version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
                var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
                await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId,
                    stops.First(s => s.MotoboyId == motoboyId).PedidoId, locked ? "locked" : "unlocked", version, sessionId);
                result.Filas.Add(await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version));
            }
            await transaction.CommitAsync();
            return result;
        }

        // =====================================================================
        // Retorno a loja
        // =====================================================================

        private sealed class ReturnSettings
        {
            public bool Require { get; init; }
            public int RadiusM { get; init; } = ReturnToStoreRules.DefaultRadiusMeters;
            public bool AutoFinish { get; init; } = true;
        }

        private sealed class ReturnSettingsRow
        {
            public bool Require { get; set; }
            public int RadiusM { get; set; }
            public bool AutoFinish { get; set; }
        }

        private sealed class RouteStateRow
        {
            public string State { get; set; } = RouteStates.Idle;
            public DateTimeOffset? Since { get; set; }
        }

        private sealed class StoreCoordRow
        {
            public double? Lat { get; set; }
            public double? Lon { get; set; }
        }

        private static async Task<ReturnSettings> ReadReturnSettingsAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid estabelecimentoId)
        {
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) return new ReturnSettings();
            var row = await connection.QuerySingleOrDefaultAsync<ReturnSettingsRow>(@"
SELECT require_return_to_store AS Require, store_return_radius_m AS RadiusM, auto_finish_route_on_return AS AutoFinish
  FROM delivery_settings WHERE estabelecimento_id = @EstabelecimentoId;",
                new { EstabelecimentoId = estabelecimentoId }, transaction);
            return row == null ? new ReturnSettings() : new ReturnSettings { Require = row.Require, RadiusM = row.RadiusM, AutoFinish = row.AutoFinish };
        }

        private static async Task ApplyReturnSettingsAsync(DeliverySettingsDto settings, NpgsqlConnection connection, Guid estabelecimentoId)
        {
            var values = await ReadReturnSettingsAsync(connection, null, estabelecimentoId);
            settings.RequireReturnToStore = values.Require;
            settings.StoreReturnRadiusM = values.RadiusM;
            settings.AutoFinishRouteOnReturn = values.AutoFinish;
        }

        private static async Task SaveReturnSettingsAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, UpdateDeliverySettingsRequest request)
        {
            if (request.RequireReturnToStore == null && request.StoreReturnRadiusM == null && request.AutoFinishRouteOnReturn == null) return;
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) throw MigrationPending();
            await connection.ExecuteAsync(@"
UPDATE delivery_settings
   SET require_return_to_store = COALESCE(@Require, require_return_to_store),
       store_return_radius_m = COALESCE(@Radius, store_return_radius_m),
       auto_finish_route_on_return = COALESCE(@Auto, auto_finish_route_on_return)
 WHERE estabelecimento_id = @EstabelecimentoId;",
                new { Require = request.RequireReturnToStore, Radius = request.StoreReturnRadiusM, Auto = request.AutoFinishRouteOnReturn, EstabelecimentoId = estabelecimentoId },
                transaction);
        }

        private static async Task<(string State, DateTimeOffset? Since)> ReadRouteStateAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, bool rules)
        {
            if (!rules) return (RouteStates.Idle, null);
            var row = await connection.QuerySingleOrDefaultAsync<RouteStateRow>(
                "SELECT route_state AS State, returning_since_utc AS Since FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId;",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            return row == null ? (RouteStates.Idle, null) : (row.State, row.Since);
        }

        /// <summary>
        /// Depois da ultima parada, a rota passa a "retornando" se o estabelecimento exige. Chamado com a
        /// fila travada, ja renumerada, antes de subir a versao. Devolve true quando entrou em retorno.
        /// </summary>
        private static async Task<bool> EnterReturningIfIdleAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) return false;
            var settings = await ReadReturnSettingsAsync(connection, transaction, estabelecimentoId);
            var left = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*)::int FROM delivery_route_stops WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND stop_status IN {ActiveStatusesSql};",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            if (!ReturnToStoreRules.ShouldEnterReturning(settings.Require, left)) return false;

            var changed = await connection.ExecuteAsync(
                "UPDATE delivery_motoboy_route SET route_state = 'returning', returning_since_utc = NOW() " +
                "WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND route_state <> 'returning';",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
            return changed > 0;
        }

        private static async Task ClearReturningAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
        {
            if (!await HasRouteRulesSchemaAsync(connection, transaction)) return;
            await connection.ExecuteAsync(
                "UPDATE delivery_motoboy_route SET route_state = 'idle', returning_since_utc = NULL " +
                "WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND route_state <> 'idle';",
                new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId }, transaction);
        }

        private static async Task EmitRouteStateEventAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId,
            string eventName, string state, string source, long version, Guid? sessionId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                eventId = Guid.NewGuid(),
                estabelecimentoId,
                motoboyId,
                state,
                source,
                version,
                occurredAtUtc = DateTimeOffset.UtcNow
            });
            await InsertOutboxAsync(connection, transaction, eventName, estabelecimentoId, motoboyId, version, payload,
                sessionId.HasValue ? new[] { sessionId.Value } : Array.Empty<Guid>());
        }

        public async Task<MotoboyQueueDto> ArriveAtStoreAsync(Guid estabelecimentoId, int motoboyId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var snapshot = await FinishReturnInternalAsync(connection, transaction, estabelecimentoId, motoboyId, "manual");
            await transaction.CommitAsync();
            return snapshot;
        }

        /// <summary>Retorno -> idle. Idempotente: sem retorno pendente so devolve a fila atual.</summary>
        private async Task<MotoboyQueueDto> FinishReturnInternalAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId, string source)
        {
            var version = await LockQueuesAsync(connection, transaction, estabelecimentoId, motoboyId);
            var rules = await HasRouteRulesSchemaAsync(connection, transaction);
            var state = await ReadRouteStateAsync(connection, transaction, estabelecimentoId, motoboyId, rules);
            if (state.State != RouteStates.Returning)
            {
                return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
            }

            await ClearReturningAsync(connection, transaction, estabelecimentoId, motoboyId);
            version = await BumpRouteVersionAsync(connection, transaction, estabelecimentoId, motoboyId);
            var sessionId = await GetSessionIdAsync(connection, transaction, motoboyId, estabelecimentoId);
            await EmitQueueEventAsync(connection, transaction, estabelecimentoId, motoboyId, null, "returned", version, sessionId);
            await EmitRouteStateEventAsync(connection, transaction, estabelecimentoId, motoboyId,
                DeliveryRealtimeEvents.DeliveryRouteReturned, RouteStates.Idle, source, version, sessionId);
            return await BuildSnapshotAsync(connection, transaction, estabelecimentoId, motoboyId, version);
        }

        public async Task<bool> TryFinishReturnByLocationAsync(Guid estabelecimentoId, int motoboyId, double latitude, double longitude)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                if (!await HasRouteRulesSchemaAsync(connection, null)) return false;

                // Caminho quente (uma posicao por poucos segundos): sai cedo se nao esta retornando.
                var returning = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM delivery_motoboy_route WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND route_state = 'returning');",
                    new { MotoboyId = motoboyId, EstabelecimentoId = estabelecimentoId });
                if (!returning) return false;

                var settings = await ReadReturnSettingsAsync(connection, null, estabelecimentoId);
                if (!settings.AutoFinish) return false;
                var store = await connection.QuerySingleOrDefaultAsync<StoreCoordRow>(
                    "SELECT latitude::double precision AS Lat, longitude::double precision AS Lon FROM estabelecimentos WHERE id = @Id;",
                    new { Id = estabelecimentoId });
                if (store == null || !ReturnToStoreRules.IsInsideStore(latitude, longitude, store.Lat, store.Lon, settings.RadiusM)) return false;

                await using var transaction = await connection.BeginTransactionAsync();
                await FinishReturnInternalAsync(connection, transaction, estabelecimentoId, motoboyId, "geofence");
                await transaction.CommitAsync();
                return true;
            }
            catch (Exception)
            {
                // A posicao do motoboy ja foi aceita: falha aqui nunca pode derrubar o recebimento dela.
                return false;
            }
        }
    }
}
