using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Delivery;
using APIBack.Hubs;
using APIBack.Model.Enum;
using APIBack.Model.Tracking;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    /// <summary>
    /// Regras puras do que a loja pode fazer em cada pedido de um incidente. Mesma funcao para exibir (GET) e para
    /// validar (POST): a tela so mostra o que o servidor ja calculou.
    /// </summary>
    public static class DisconnectIncidentRules
    {
        public const string Pendente = "pendente";
        public const string Devolvido = "devolvido";
        public const string Resolvido = "resolvido";

        public static IncidenteAcaoDisponivelDto[] ItemActions(bool reconectado, bool collected, string state, bool excecao)
        {
            if (state == Resolvido)
                return IncidentActions.ItemLevel.Select(a => Blocked(a, "Pedido ja resolvido.")).ToArray();
            var atStore = !collected || state == Devolvido;
            return new[]
            {
                Action(IncidentActions.ReofertarMesmoMotoboy, reconectado, "O motoboy ainda nao abriu um novo turno.", false, false),
                Action(IncidentActions.Redistribuir, atStore,
                    "A mercadoria esta com o motoboy: confirme a devolucao fisica antes de redistribuir.", false, false),
                Action(IncidentActions.ConfirmarDevolucaoFisica, collected && state == Pendente,
                    collected ? "A devolucao fisica ja foi confirmada." : "O pedido nao foi retirado; nao ha devolucao.", false, true),
                Action(IncidentActions.RegistrarFalha, collected && (state == Devolvido || excecao),
                    collected ? "Exige devolucao confirmada ou autorizacao excepcional." : "O pedido nao foi retirado.", true, true),
                Action(IncidentActions.ConcluirManual, true, null, true, true),
                Action(IncidentActions.Cancelar, atStore || excecao,
                    "Pedido com o motoboy: confirme a devolucao ou use autorizacao excepcional.", true, true)
            };
        }

        public static IncidenteAcaoDisponivelDto[] LevelActions(string incidentStatus) => new[]
        {
            Action(IncidentActions.AguardarContato, incidentStatus == "aberto",
                incidentStatus == "resolvido" ? "Incidente resolvido." : "Ja aguardando retorno do motoboy.", false, false),
            Action(IncidentActions.RegistrarContato, incidentStatus != "resolvido", "Incidente resolvido.", true, false)
        };

        private static IncidenteAcaoDisponivelDto Action(string name, bool allowed, string? blocked, bool justify, bool confirm) => new()
        {
            Acao = name, Permitida = allowed, MotivoBloqueio = allowed ? null : blocked,
            ExigeJustificativa = justify, ExigeConfirmacao = confirm
        };

        private static IncidenteAcaoDisponivelDto Blocked(string name, string reason) => Action(name, false, reason, false, false);

        public static string ReasonText(string reason) => reason switch
        {
            "inactivity_7h" => "Turno encerrado por 7 horas sem atividade.",
            "client_end" or "motoboy_offline" => "O motoboy encerrou o turno.",
            "logout" => "O motoboy saiu da conta.",
            "explicit_switch" => "O motoboy trocou de estabelecimento.",
            "link_revoked" => "O vinculo com a loja foi removido.",
            "heartbeat_timeout" => "Turno expirado por falta de conexao.",
            _ when reason.Contains("login") || reason.Contains("replaced") => "A conta foi acessada em outro aparelho.",
            _ => "Turno encerrado."
        };
    }

    /// <summary>Dados de um item que a rotina de execucao precisa para agir fora da transacao.</summary>
    public sealed class IncidentItemPlan
    {
        public int PedidoId { get; set; }
        public bool Collected { get; set; }
        public string State { get; set; } = DisconnectIncidentRules.Pendente;
        public int PreviousMotoboyId { get; set; }
    }

    public sealed class IncidentActionPlan
    {
        public AcaoIncidenteResponse? Replay { get; set; }
        public long ActionId { get; set; }
        public int PreviousMotoboyId { get; set; }
        public List<IncidentItemPlan> Items { get; set; } = new();
        public List<AcaoIncidenteResultadoDto> Rejected { get; set; } = new();
    }

    public sealed class IncidentItemOutcome
    {
        public int PedidoId { get; set; }
        public bool Ok { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
        /// <summary>Novo estado do item (devolvido | resolvido) quando Ok.</summary>
        public string? NewState { get; set; }
        public string? Resolution { get; set; }
    }

    public sealed class DisconnectIncidentRepository
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly NpgsqlDataSource _dataSource;

        public DisconnectIncidentRepository(NpgsqlDataSource dataSource) =>
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        // =====================================================================
        // Abertura (chamada dentro da transacao que encerra o turno)
        // =====================================================================

        /// <summary>
        /// Cria o incidente do turno encerrado, uma unica vez (unique em session_id), junto com a liberacao dos pedidos nao retirados.
        /// Sem pedido afetado nao ha incidente. Sem o schema novo (banco antigo) o encerramento segue como antes.
        /// </summary>
        internal static async Task<Guid?> OpenAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, OperationalSessionRecord session,
            string reason, int? actorUserId, DateTimeOffset serverNow)
        {
            if (session.Origin != "mobile") return null;
            if (!await connection.ExecuteScalarAsync<bool>(@"
SELECT to_regclass('delivery_motoboy_disconnect_incidents') IS NOT NULL
   AND to_regclass('delivery_route_stops') IS NOT NULL AND to_regclass('delivery_motoboy_route') IS NOT NULL
   AND to_regclass('pedido') IS NOT NULL;", transaction: transaction)) return null;

            var stops = await PedidoQueueRepository.CaptureAndReleaseOnDisconnectAsync(
                connection, transaction, session.EstabelecimentoId, session.MotoboyId);
            if (stops.Count == 0) return null;

            var location = await connection.QueryFirstOrDefaultAsync<LocationRow>(@"
SELECT latitude AS Latitude, longitude AS Longitude, accuracy_meters AS Accuracy, captured_at_utc AS CapturedAtUtc
  FROM motoboy_location_current WHERE motoboy_id = @M ORDER BY received_at_utc DESC LIMIT 1;",
                new { M = session.MotoboyId }, transaction);
            var lastContact = session.LastSeenAtUtc.HasValue && session.LastSeenAtUtc > session.LastHeartbeatAtUtc
                ? session.LastSeenAtUtc : session.LastHeartbeatAtUtc;
            var incidentId = Guid.NewGuid();
            var inserted = await connection.ExecuteAsync(@"
INSERT INTO delivery_motoboy_disconnect_incidents (
    incident_id, estabelecimento_id, motoboy_id, session_id, end_reason, ended_by_user_id, client_platform,
    opened_at_utc, turn_ended_at_utc, last_contact_at_utc, last_human_activity_at_utc,
    last_latitude, last_longitude, last_accuracy_meters, last_location_captured_at_utc)
VALUES (@Id, @E, @M, @S, @Reason, @Actor, @Platform, @Now, @Now, @Contact, @Human, @Lat, @Lon, @Acc, @Captured)
ON CONFLICT (session_id) DO NOTHING;",
                new
                {
                    Id = incidentId, E = session.EstabelecimentoId, M = session.MotoboyId, S = session.SessionId, Reason = reason,
                    Actor = actorUserId, Platform = session.DeviceType == "web" ? "web" : "native", Now = serverNow,
                    Contact = lastContact, Human = session.LastHumanActivityAtUtc,
                    Lat = location?.Latitude, Lon = location?.Longitude, Acc = location?.Accuracy, Captured = location?.CapturedAtUtc
                }, transaction);
            if (inserted == 0) return null;

            foreach (var stop in stops)
            {
                await connection.ExecuteAsync(@"
INSERT INTO delivery_motoboy_disconnect_incident_items (
    incident_id, estabelecimento_id, pedido_id, stop_id, previous_motoboy_id, collected,
    stop_status_at_close, pedido_status_at_close, picked_up_at_utc, payment_snapshot)
VALUES (@Incident, @E, @Pedido, @Stop, @M, @Collected, @StopStatus, @PedidoStatus, @PickedUp, @Payment::jsonb)
ON CONFLICT (incident_id, pedido_id) DO NOTHING;",
                    new
                    {
                        Incident = incidentId, E = session.EstabelecimentoId, Pedido = stop.PedidoId, Stop = stop.StopId,
                        M = session.MotoboyId, Collected = stop.Collected, stop.StopStatus, stop.PedidoStatus,
                        PickedUp = stop.PickedUpAtUtc,
                        Payment = JsonSerializer.Serialize(new { valor = stop.Value, tipoPagamento = stop.TipoPagamento,
                            statusPagamento = stop.StatusPagamento, troco = stop.Troco })
                    }, transaction);
            }

            await EmitAsync(connection, transaction, session.EstabelecimentoId, incidentId, "delivery.attention.opened");
            return incidentId;
        }

        // =====================================================================
        // Leitura
        // =====================================================================

        public async Task<IncidenteAtencaoListaDto> ListAsync(
            Guid estabelecimentoId, bool includeResolved, DateTimeOffset? since, bool exceptional)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var changed = await ReconcileAsync(connection, transaction, estabelecimentoId, null);
            await FinalizeAsync(connection, transaction, estabelecimentoId, changed, null);
            var list = await LoadAsync(connection, transaction, estabelecimentoId, null, includeResolved, since, exceptional);
            var now = await connection.ExecuteScalarAsync<DateTimeOffset>("SELECT NOW();", transaction: transaction);
            await transaction.CommitAsync();
            return new IncidenteAtencaoListaDto { ServerTimeUtc = now, Incidentes = list };
        }

        public async Task<IncidenteAtencaoDto?> GetAsync(Guid estabelecimentoId, Guid incidentId, bool exceptional)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return (await LoadAsync(connection, null, estabelecimentoId, incidentId, true, null, exceptional)).FirstOrDefault();
        }

        // =====================================================================
        // Acoes: fase 1 (valida e reserva), operacoes da fila (fora daqui), fase 2 (grava o resultado)
        // =====================================================================

        public async Task<IncidentActionPlan> BeginActionAsync(
            Guid estabelecimentoId, int actorUserId, bool exceptional, Guid incidentId, AcaoIncidenteRequest request)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var header = await connection.QuerySingleOrDefaultAsync<MiniHeader>(@"
SELECT version AS Version, status AS Status, motoboy_id AS MotoboyId
  FROM delivery_motoboy_disconnect_incidents
 WHERE incident_id = @Id AND estabelecimento_id = @E FOR UPDATE;",
                new { Id = incidentId, E = estabelecimentoId }, transaction)
                ?? throw new DeliveryDomainException(404, "INCIDENT_NOT_FOUND", "Incidente nao encontrado neste estabelecimento.");

            var existing = await connection.QuerySingleOrDefaultAsync<ActionRow>(@"
SELECT id AS Id, state AS State, result::text AS Result, created_at_utc AS CreatedAtUtc
  FROM delivery_motoboy_disconnect_incident_actions
 WHERE incident_id = @Id AND idempotency_key = @Key FOR UPDATE;",
                new { Id = incidentId, Key = request.IdempotencyKey }, transaction);
            if (existing is { State: "concluida" })
            {
                var replayDto = (await LoadAsync(connection, transaction, estabelecimentoId, incidentId, true, null, exceptional)).First();
                await transaction.CommitAsync();
                return new IncidentActionPlan
                {
                    Replay = new AcaoIncidenteResponse
                    {
                        Incidente = replayDto,
                        Resultados = JsonSerializer.Deserialize<List<AcaoIncidenteResultadoDto>>(existing.Result ?? "[]", Json)
                            ?? new List<AcaoIncidenteResultadoDto>()
                    }
                };
            }
            if (existing != null && existing.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-2))
                throw new DeliveryDomainException(409, "ACAO_EM_ANDAMENTO", "Esta acao ja esta sendo processada.");

            var changed = await ReconcileAsync(connection, transaction, estabelecimentoId, incidentId);
            if (changed.Count > 0) await FinalizeAsync(connection, transaction, estabelecimentoId, changed, null);
            var incident = (await LoadAsync(connection, transaction, estabelecimentoId, incidentId, true, null, exceptional)).First();
            if (request.VersaoEsperada != incident.Version)
                throw new DeliveryDomainException(409, "VERSAO_DESATUALIZADA",
                    "O incidente mudou desde a ultima leitura. Confira o estado atual e tente de novo.", incident);

            var action = request.TipoAcao!;
            var plan = new IncidentActionPlan { PreviousMotoboyId = header.MotoboyId };
            if (Array.IndexOf(IncidentActions.IncidentLevel, action) >= 0)
            {
                var level = DisconnectIncidentRules.LevelActions(incident.Status).First(a => a.Acao == action);
                if (!level.Permitida) throw new DeliveryDomainException(422, "ACAO_NAO_PERMITIDA", level.MotivoBloqueio ?? "Acao nao permitida.");
                if (level.ExigeJustificativa && string.IsNullOrWhiteSpace(request.Motivo))
                    throw new DeliveryDomainException(422, "JUSTIFICATIVA_OBRIGATORIA", "Informe a justificativa.");
            }
            else
            {
                var wanted = request.PedidoIds is { Count: > 0 }
                    ? request.PedidoIds.Distinct().ToList()
                    : incident.Pedidos.Where(p => p.Situacao == DisconnectIncidentRules.Pendente).Select(p => p.PedidoId).ToList();
                if (wanted.Count == 0) throw new DeliveryDomainException(422, "ACAO_NAO_PERMITIDA", "Nenhum pedido pendente neste incidente.");
                var rows = await connection.QueryAsync<IncidentItemPlan>(@"
SELECT pedido_id AS PedidoId, collected AS Collected, state AS State, previous_motoboy_id AS PreviousMotoboyId
  FROM delivery_motoboy_disconnect_incident_items WHERE incident_id = @Id AND pedido_id = ANY(@Ids);",
                    new { Id = incidentId, Ids = wanted.ToArray() }, transaction);
                var byId = rows.ToDictionary(r => r.PedidoId);
                var firstBlock = (string?)null;
                foreach (var pedidoId in wanted)
                {
                    if (!byId.TryGetValue(pedidoId, out var item))
                    {
                        plan.Rejected.Add(new() { PedidoId = pedidoId, Code = "PEDIDO_NOT_IN_INCIDENT", Mensagem = "Pedido nao pertence a este incidente." });
                        continue;
                    }
                    var rule = DisconnectIncidentRules.ItemActions(incident.Motoboy.Reconectado, item.Collected, item.State, exceptional)
                        .First(a => a.Acao == action);
                    if (!rule.Permitida)
                    {
                        firstBlock ??= rule.MotivoBloqueio;
                        plan.Rejected.Add(new() { PedidoId = pedidoId, Code = "ACAO_NAO_PERMITIDA", Mensagem = rule.MotivoBloqueio });
                        continue;
                    }
                    if (rule.ExigeJustificativa && string.IsNullOrWhiteSpace(request.Motivo))
                        throw new DeliveryDomainException(422, "JUSTIFICATIVA_OBRIGATORIA", "Informe a justificativa.");
                    plan.Items.Add(item);
                }
                if (action == IncidentActions.Redistribuir && plan.Items.Count > 0)
                {
                    if (request.MotoboyDestinoId is not > 0)
                        throw new DeliveryDomainException(422, "DESTINO_OBRIGATORIO", "Informe o motoboy de destino.");
                    if (request.MotoboyDestinoId == header.MotoboyId)
                        throw new DeliveryDomainException(422, "DESTINO_INVALIDO",
                            "Para devolver ao mesmo motoboy use reofertar_mesmo_motoboy.");
                }
                if (plan.Items.Count == 0)
                    throw new DeliveryDomainException(422, "ACAO_NAO_PERMITIDA", firstBlock ?? "Nenhum pedido pode receber esta acao.");
            }

            if (existing == null)
            {
                plan.ActionId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO delivery_motoboy_disconnect_incident_actions (
    incident_id, estabelecimento_id, idempotency_key, action_type, pedido_ids, destination_motoboy_id, reason, expected_version, actor_user_id)
VALUES (@Id, @E, @Key, @Action, @Pedidos, @Dest, @Reason, @Version, @Actor) RETURNING id;",
                    new
                    {
                        Id = incidentId, E = estabelecimentoId, Key = request.IdempotencyKey, Action = action,
                        Pedidos = plan.Items.Select(i => i.PedidoId).ToArray(), Dest = request.MotoboyDestinoId,
                        Reason = request.Motivo?.Trim(), Version = request.VersaoEsperada, Actor = actorUserId
                    }, transaction);
            }
            else
            {
                plan.ActionId = existing.Id;
                await connection.ExecuteAsync("UPDATE delivery_motoboy_disconnect_incident_actions SET created_at_utc = NOW() WHERE id = @Id;",
                    new { existing.Id }, transaction);
            }
            // Reserva a versao: uma segunda acao com a mesma versao esperada toma 409 em vez de repetir a operacao.
            await connection.ExecuteAsync(
                "UPDATE delivery_motoboy_disconnect_incidents SET version = version + 1, updated_at_utc = NOW() WHERE incident_id = @Id;",
                new { Id = incidentId }, transaction);
            await transaction.CommitAsync();
            return plan;
        }

        public async Task<AcaoIncidenteResponse> CompleteActionAsync(
            Guid estabelecimentoId, int actorUserId, bool exceptional, Guid incidentId, AcaoIncidenteRequest request,
            IncidentActionPlan plan, IReadOnlyList<IncidentItemOutcome> outcomes)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(
                "SELECT 1 FROM delivery_motoboy_disconnect_incidents WHERE incident_id = @Id AND estabelecimento_id = @E FOR UPDATE;",
                new { Id = incidentId, E = estabelecimentoId }, transaction);

            foreach (var outcome in outcomes.Where(o => o.Ok))
            {
                await connection.ExecuteAsync(@"
UPDATE delivery_motoboy_disconnect_incident_items
   SET state = @State, resolution = CASE WHEN @State = 'resolvido' THEN @Resolution ELSE resolution END,
       resolved_at_utc = CASE WHEN @State = 'resolvido' THEN NOW() ELSE resolved_at_utc END,
       resolved_by_user_id = CASE WHEN @State = 'resolvido' THEN @Actor ELSE resolved_by_user_id END,
       return_confirmed_at_utc = CASE WHEN @State = 'devolvido' THEN NOW() ELSE return_confirmed_at_utc END,
       return_confirmed_by_user_id = CASE WHEN @State = 'devolvido' THEN @Actor ELSE return_confirmed_by_user_id END,
       updated_at_utc = NOW()
 WHERE incident_id = @Id AND pedido_id = @Pedido;",
                    new { Id = incidentId, Pedido = outcome.PedidoId, State = outcome.NewState, outcome.Resolution, Actor = actorUserId }, transaction);
            }

            if (request.TipoAcao == IncidentActions.AguardarContato)
                await connection.ExecuteAsync(@"
UPDATE delivery_motoboy_disconnect_incidents SET status = 'aguardando_retorno'
 WHERE incident_id = @Id AND status = 'aberto';", new { Id = incidentId }, transaction);

            var results = plan.Rejected
                .Concat(outcomes.Select(o => new AcaoIncidenteResultadoDto { PedidoId = o.PedidoId, Ok = o.Ok, Code = o.Code, Mensagem = o.Message }))
                .ToList();
            await connection.ExecuteAsync(@"
UPDATE delivery_motoboy_disconnect_incident_actions
   SET state = 'concluida', result = @Result::jsonb, completed_at_utc = NOW()
 WHERE id = @Id;", new { Id = plan.ActionId, Result = JsonSerializer.Serialize(results, Json) }, transaction);

            await FinalizeAsync(connection, transaction, estabelecimentoId, new List<Guid> { incidentId }, actorUserId);
            var dto = (await LoadAsync(connection, transaction, estabelecimentoId, incidentId, true, null, exceptional)).First();
            await transaction.CommitAsync();
            return new AcaoIncidenteResponse { Incidente = dto, Resultados = results };
        }

        // =====================================================================
        // Reconciliacao e fechamento
        // =====================================================================

        /// <summary>
        /// Itens cujo pedido seguiu por outro caminho (atribuido pelo painel, transferido, concluido, cancelado...) saem do
        /// incidente sozinhos. Nao desfaz nada, so reconhece o que ja aconteceu.
        /// </summary>
        internal static async Task<List<Guid>> ReconcileAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, Guid? incidentId)
        {
            var ids = await connection.QueryAsync<Guid>(@"
UPDATE delivery_motoboy_disconnect_incident_items it
   SET state = 'resolvido', resolved_at_utc = NOW(), updated_at_utc = NOW(),
       resolution = CASE WHEN p.status_pedido IN (3, 4, 7) THEN 'pedido_encerrado'
                         WHEN it.collected AND it.state = 'pendente' THEN 'transferido'
                         ELSE 'reatribuido' END
  FROM pedido p
 WHERE p.id = it.pedido_id AND it.estabelecimento_id = @E AND it.state <> 'resolvido'
   AND (@Id::uuid IS NULL OR it.incident_id = @Id)
   AND (p.status_pedido IN (3, 4, 7)
        OR (p.status_pedido IN (2, 5, 8) AND p.motoboy_responsavel IS NOT NULL
            AND ((NOT it.collected OR it.state = 'devolvido') OR p.motoboy_responsavel IS DISTINCT FROM it.previous_motoboy_id)))
RETURNING it.incident_id;", new { E = estabelecimentoId, Id = incidentId }, transaction);
            return ids.Distinct().ToList();
        }

        /// <summary>Sobe a versao, fecha o incidente sem pendencias e avisa o painel (resolved ou updated).</summary>
        internal static async Task FinalizeAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, List<Guid> incidentIds, int? actorUserId)
        {
            if (incidentIds.Count == 0) return;
            var rows = await connection.QueryAsync<FinalizedRow>(@"
UPDATE delivery_motoboy_disconnect_incidents i
   SET version = i.version + 1,
       status = CASE WHEN EXISTS (SELECT 1 FROM delivery_motoboy_disconnect_incident_items x
                                   WHERE x.incident_id = i.incident_id AND x.state <> 'resolvido') THEN i.status ELSE 'resolvido' END,
       resolved_at_utc = CASE WHEN i.status <> 'resolvido' AND NOT EXISTS (SELECT 1 FROM delivery_motoboy_disconnect_incident_items x
                                   WHERE x.incident_id = i.incident_id AND x.state <> 'resolvido') THEN NOW() ELSE i.resolved_at_utc END,
       resolved_by_user_id = CASE WHEN i.status <> 'resolvido' AND NOT EXISTS (SELECT 1 FROM delivery_motoboy_disconnect_incident_items x
                                   WHERE x.incident_id = i.incident_id AND x.state <> 'resolvido') THEN @Actor ELSE i.resolved_by_user_id END,
       updated_at_utc = NOW()
 WHERE i.incident_id = ANY(@Ids) AND i.estabelecimento_id = @E
RETURNING i.incident_id AS Id, i.status AS Status;",
                new { Ids = incidentIds.ToArray(), E = estabelecimentoId, Actor = actorUserId }, transaction);
            foreach (var row in rows.ToList())
                await EmitAsync(connection, transaction, estabelecimentoId, row.Id,
                    row.Status == "resolvido" ? "delivery.attention.resolved" : "delivery.attention.updated");
        }

        private static async Task EmitAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, Guid incidentId, string eventName)
        {
            // O evento e igual para todos os atendentes: acoes excepcionais aparecem bloqueadas; o GET traz o calculo de cada usuario.
            var dto = (await LoadAsync(connection, transaction, estabelecimentoId, incidentId, true, null, false)).FirstOrDefault();
            if (dto == null) return;
            var eventId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 2, eventId, estabelecimentoId, incidenteId = incidentId, version = dto.Version,
                occurredAtUtc = DateTimeOffset.UtcNow, incidente = dto
            }, Json);
            await connection.ExecuteAsync(@"
INSERT INTO delivery_realtime_outbox (
    event_id, event_name, target_group, estabelecimento_id, motoboy_id,
    session_id, session_epoch, aggregate_version, payload, occurred_at_utc)
VALUES (@EventId, @EventName, @Target, @E, @M, NULL, NULL, @Version, @Payload::jsonb, NOW());",
                new
                {
                    EventId = eventId, EventName = eventName, Target = DeliveryRealtimeEvents.EstablishmentGroup(estabelecimentoId),
                    E = estabelecimentoId, M = dto.Motoboy.Id, Version = (long)dto.Version, Payload = payload
                }, transaction);
        }

        // =====================================================================
        // Montagem do DTO
        // =====================================================================

        internal static async Task<List<IncidenteAtencaoDto>> LoadAsync(
            NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid estabelecimentoId, Guid? incidentId,
            bool includeResolved, DateTimeOffset? since, bool exceptional)
        {
            var headers = (await connection.QueryAsync<HeaderRow>(@"
SELECT i.incident_id AS IncidentId, i.motoboy_id AS MotoboyId, i.end_reason AS EndReason, i.status AS Status, i.version AS Version,
       i.opened_at_utc AS OpenedAtUtc, i.turn_ended_at_utc AS TurnEndedAtUtc, i.last_contact_at_utc AS LastContactAtUtc,
       i.last_human_activity_at_utc AS LastHumanActivityAtUtc, i.last_latitude AS Latitude, i.last_longitude AS Longitude,
       i.last_accuracy_meters AS Accuracy, i.last_location_captured_at_utc AS CapturedAtUtc, i.resolved_at_utc AS ResolvedAtUtc,
       u.nome::text AS ResolvedByName, COALESCE(m.nome, '') AS MotoboyNome, m.avatar AS MotoboyAvatar,
       EXISTS (SELECT 1 FROM motoboy_active_sessions s
                WHERE s.motoboy_id = i.motoboy_id AND s.id_estabelecimento = i.estabelecimento_id
                  AND s.session_id <> i.session_id AND s.origin = 'mobile'
                  AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL AND s.expires_at_utc > NOW()) AS Reconnected
  FROM delivery_motoboy_disconnect_incidents i
  JOIN motoboy m ON m.id = i.motoboy_id
  LEFT JOIN usuario u ON u.id = i.resolved_by_user_id
 WHERE i.estabelecimento_id = @E AND (@Id::uuid IS NULL OR i.incident_id = @Id)
   AND (@All OR i.status <> 'resolvido') AND (@Since::timestamptz IS NULL OR i.opened_at_utc >= @Since)
 ORDER BY (i.status = 'resolvido'), i.opened_at_utc DESC
 LIMIT 200;", new { E = estabelecimentoId, Id = incidentId, All = includeResolved, Since = since }, transaction)).ToList();
            if (headers.Count == 0) return new List<IncidenteAtencaoDto>();

            var ids = headers.Select(h => h.IncidentId).ToArray();
            var items = (await connection.QueryAsync<ItemRow>(@"
SELECT it.incident_id AS IncidentId, it.pedido_id AS PedidoId, it.collected AS Collected, it.state AS State,
       it.resolution AS Resolution, it.picked_up_at_utc AS PickedUpAtUtc, it.return_confirmed_at_utc AS ReturnConfirmedAtUtc,
       p.status_pedido AS PedidoStatus,
       (SELECT s.stop_status FROM delivery_route_stops s
         WHERE s.pedido_id = it.pedido_id AND s.estabelecimento_id = it.estabelecimento_id
           AND s.stop_status IN ('assigned', 'en_route') LIMIT 1) AS StopStatus,
       p.nome_cliente::text AS ClienteNome, p.entrega_bairro::text AS Bairro, p.value::text AS ValueText,
       p.tipo_pagamento::text AS TipoPagamento
  FROM delivery_motoboy_disconnect_incident_items it
  JOIN pedido p ON p.id = it.pedido_id
 WHERE it.incident_id = ANY(@Ids) AND it.estabelecimento_id = @E
 ORDER BY it.id;", new { Ids = ids, E = estabelecimentoId }, transaction)).ToList();
            var history = (await connection.QueryAsync<HistoryRow>(@"
SELECT a.id AS Id, a.incident_id AS IncidentId, a.action_type AS Action, a.pedido_ids AS PedidoIds, u.nome::text AS UserName,
       a.reason AS Reason, COALESCE(a.completed_at_utc, a.created_at_utc) AS At
  FROM delivery_motoboy_disconnect_incident_actions a
  LEFT JOIN usuario u ON u.id = a.actor_user_id
 WHERE a.incident_id = ANY(@Ids) AND a.estabelecimento_id = @E AND a.state = 'concluida'
 ORDER BY a.id;", new { Ids = ids, E = estabelecimentoId }, transaction)).ToList();

            return headers.Select(h =>
            {
                var mine = items.Where(i => i.IncidentId == h.IncidentId).Select(i => new IncidentePedidoDto
                {
                    PedidoId = i.PedidoId,
                    Numero = i.PedidoId.ToString(CultureInfo.InvariantCulture),
                    ClienteNome = i.ClienteNome,
                    Bairro = i.Bairro,
                    Valor = ParseMoney(i.ValueText),
                    TipoPagamento = i.TipoPagamento,
                    StatusPedido = StatusPedidoExtensions.ToApiKey(i.PedidoStatus),
                    Coletado = i.Collected,
                    ColetadoEmUtc = i.PickedUpAtUtc,
                    DevolucaoConfirmada = i.ReturnConfirmedAtUtc.HasValue,
                    Estagio = i.Collected && i.State == DisconnectIncidentRules.Pendente
                        ? (i.StopStatus == "en_route" ? "em_rota" : "coletado") : "na_loja",
                    Situacao = i.State == DisconnectIncidentRules.Resolvido ? "resolvido" : "pendente",
                    Resolucao = i.Resolution,
                    Acoes = DisconnectIncidentRules.ItemActions(h.Reconnected, i.Collected, i.State, exceptional)
                }).ToList();
                return new IncidenteAtencaoDto
                {
                    Id = h.IncidentId,
                    Version = h.Version,
                    Status = h.Status,
                    Motivo = h.EndReason,
                    MotivoTexto = DisconnectIncidentRules.ReasonText(h.EndReason),
                    AbertoEmUtc = h.OpenedAtUtc,
                    TurnoEncerradoEmUtc = h.TurnEndedAtUtc,
                    ResolvidoEmUtc = h.ResolvedAtUtc,
                    ResolvidoPorNome = h.ResolvedByName,
                    Motoboy = new IncidenteMotoboyDto
                    {
                        Id = h.MotoboyId, Nome = h.MotoboyNome, Avatar = h.MotoboyAvatar, Reconectado = h.Reconnected,
                        UltimoContatoUtc = h.LastContactAtUtc, UltimaAtividadeUtc = h.LastHumanActivityAtUtc
                    },
                    UltimaLocalizacao = h.Latitude.HasValue && h.Longitude.HasValue && h.CapturedAtUtc.HasValue
                        ? new IncidenteLocalizacaoDto { Latitude = h.Latitude.Value, Longitude = h.Longitude.Value,
                            CapturedAtUtc = h.CapturedAtUtc.Value, AccuracyMeters = h.Accuracy }
                        : null,
                    Pedidos = mine,
                    AcoesIncidente = DisconnectIncidentRules.LevelActions(h.Status),
                    Historico = history.Where(x => x.IncidentId == h.IncidentId).Select(x => new IncidenteHistoricoDto
                    {
                        Id = x.Id, Acao = x.Action, PedidoIds = x.PedidoIds ?? Array.Empty<int>(),
                        UsuarioNome = x.UserName, Justificativa = x.Reason, OcorridoEmUtc = x.At
                    }).ToList()
                };
            }).ToList();
        }

        private static decimal? ParseMoney(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var normalized = text.Contains('.') ? text : text.Replace(',', '.');
            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        private sealed class MiniHeader
        {
            public int Version { get; set; }
            public string Status { get; set; } = "";
            public int MotoboyId { get; set; }
        }

        private sealed class FinalizedRow
        {
            public Guid Id { get; set; }
            public string Status { get; set; } = "";
        }

        private sealed class LocationRow
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double? Accuracy { get; set; }
            public DateTimeOffset CapturedAtUtc { get; set; }
        }

        private sealed class ActionRow
        {
            public long Id { get; set; }
            public string State { get; set; } = "";
            public string? Result { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
        }

        private sealed class HeaderRow
        {
            public Guid IncidentId { get; set; }
            public int MotoboyId { get; set; }
            public string EndReason { get; set; } = "";
            public string Status { get; set; } = "aberto";
            public int Version { get; set; }
            public DateTimeOffset OpenedAtUtc { get; set; }
            public DateTimeOffset TurnEndedAtUtc { get; set; }
            public DateTimeOffset? LastContactAtUtc { get; set; }
            public DateTimeOffset? LastHumanActivityAtUtc { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public double? Accuracy { get; set; }
            public DateTimeOffset? CapturedAtUtc { get; set; }
            public DateTimeOffset? ResolvedAtUtc { get; set; }
            public string? ResolvedByName { get; set; }
            public string MotoboyNome { get; set; } = "";
            public string? MotoboyAvatar { get; set; }
            public bool Reconnected { get; set; }
        }

        private sealed class ItemRow
        {
            public Guid IncidentId { get; set; }
            public int PedidoId { get; set; }
            public bool Collected { get; set; }
            public string State { get; set; } = "pendente";
            public string? Resolution { get; set; }
            public DateTimeOffset? PickedUpAtUtc { get; set; }
            public DateTimeOffset? ReturnConfirmedAtUtc { get; set; }
            public int? PedidoStatus { get; set; }
            public string? StopStatus { get; set; }
            public string? ClienteNome { get; set; }
            public string? Bairro { get; set; }
            public string? ValueText { get; set; }
            public string? TipoPagamento { get; set; }
        }

        private sealed class HistoryRow
        {
            public long Id { get; set; }
            public Guid IncidentId { get; set; }
            public string Action { get; set; } = "";
            public int[]? PedidoIds { get; set; }
            public string? UserName { get; set; }
            public string? Reason { get; set; }
            public DateTimeOffset At { get; set; }
        }
    }
}
