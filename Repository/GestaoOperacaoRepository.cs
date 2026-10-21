using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class GestaoOperacaoRepository(NpgsqlDataSource source) : IGestaoOperacaoRepository
{
    public async Task<IReadOnlyList<OperacaoLojaRow>> ListarLojasAsync(Guid empresaId, int userId, bool todasAsLojas, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OperacaoLojaRow>(new CommandDefinition("""
SELECT e.id AS Id, COALESCE(e.nome_fantasia,'') AS Nome, COALESCE(e.situacao_comercial,'ativo') AS Situacao,
       COALESCE(e.modulos_ativos::text[], ARRAY[]::text[]) AS Modulos
  FROM estabelecimentos e
 WHERE e.id_empresa = @Empresa
   AND e.ativo IS NOT FALSE AND LOWER(COALESCE(e.status,'ativo')) IN ('ativo','trial')
   AND (@Todas OR EXISTS (SELECT 1 FROM usuario_estabelecimentos ue
                           WHERE ue.id_usuario = @User AND ue.id_estabelecimento = e.id
                             AND ue.ativo IS NOT FALSE AND LOWER(COALESCE(ue.status,'ativo')) = 'ativo'))
 ORDER BY e.nome_fantasia
""", new { Empresa = empresaId, User = userId, Todas = todasAsLojas }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<OperacaoContadoresRow>> ContadoresAsync(IReadOnlyCollection<Guid> lojas, CancellationToken ct = default)
    {
        if (lojas.Count == 0) return Array.Empty<OperacaoContadoresRow>();
        await using var connection = await source.OpenConnectionAsync(ct);
        var ids = lojas.ToArray();
        // Status: 1 pendente; 2 em rota, 5 atribuido e 8 aguardando motoboy contam como "em rota" (StatusPedido.cs).
        var rows = await connection.QueryAsync<OperacaoContadoresRow>(new CommandDefinition("""
WITH ped AS (
    SELECT p.id_estabelecimento AS loja,
           COUNT(*) FILTER (WHERE COALESCE(p.status_pedido,1) = 1) AS pendentes,
           COUNT(*) FILTER (WHERE COALESCE(p.status_pedido,1) IN (2,5,8)) AS em_rota
      FROM pedido p
     WHERE p.id_estabelecimento = ANY(@Ids) AND COALESCE(p.status_pedido,1) IN (1,2,5,8)
     GROUP BY p.id_estabelecimento),
mb AS (
    SELECT s.id_estabelecimento AS loja, COUNT(DISTINCT s.motoboy_id) AS online
      FROM motoboy_active_sessions s
      JOIN motoboy m ON m.id = s.motoboy_id AND COALESCE(m.is_simulated, FALSE) = FALSE
     WHERE s.id_estabelecimento = ANY(@Ids) AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL
       AND s.expires_at_utc > NOW() AND s.paused_at_utc IS NULL
     GROUP BY s.id_estabelecimento),
tr AS (
    SELECT t.estabelecimento_id AS loja, COUNT(*) AS pendentes
      FROM delivery_transfer_requests t
     WHERE t.estabelecimento_id = ANY(@Ids) AND t.status = 'pending_approval'
     GROUP BY t.estabelecimento_id)
SELECT i.id AS EstabelecimentoId,
       COALESCE(ped.pendentes,0)::int AS Pendentes, COALESCE(ped.em_rota,0)::int AS EmRota,
       COALESCE(mb.online,0)::int AS MotoboysOnline, COALESCE(tr.pendentes,0)::int AS TransferenciasPendentes
  FROM unnest(@Ids) AS i(id)
  LEFT JOIN ped ON ped.loja = i.id
  LEFT JOIN mb ON mb.loja = i.id
  LEFT JOIN tr ON tr.loja = i.id
""", new { Ids = ids }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<OperacaoIfoodRow>> AlertasIfoodAsync(IReadOnlyCollection<Guid> lojas, CancellationToken ct = default)
    {
        if (lojas.Count == 0) return Array.Empty<OperacaoIfoodRow>();
        await using var connection = await source.OpenConnectionAsync(ct);
        try
        {
            // Mesmas regras de IfoodReconciliationRepository.GetStoreAlertsAsync (aceite 3/6 min, revisao, backlog, falhas, comando e
            // conclusao incertos, disputa aberta), contadas por loja: critico soma as de gravidade critical, aviso as de warning.
            var rows = await connection.QueryAsync<OperacaoIfoodRow>(new CommandDefinition("""
WITH waiting AS (
    SELECT s.estabelecimento_id AS loja,
           COALESCE(CASE WHEN s.payload->>'createdAt' ~ '^\d{4}-\d{2}-\d{2}T.*(Z|[+-]\d{2}:\d{2})$'
                         THEN (s.payload->>'createdAt')::timestamptz END, l.created_at_utc) AS created_at
      FROM ifood_order_snapshots s
      JOIN ifood_order_links l ON l.merchant_id = s.merchant_id AND l.external_order_id = s.external_order_id
     WHERE s.estabelecimento_id = ANY(@Ids) AND s.preparation_state = 'awaiting_acceptance'
       AND COALESCE(s.remote_status,'PLACED') = 'PLACED' AND s.order_type IN ('DELIVERY','TAKEOUT')
       AND COALESCE(s.payload->'zippyRecovery','false'::jsonb) <> 'true'::jsonb),
alerts AS (
    SELECT loja, 'critical' AS sev, COUNT(*) AS n FROM waiting WHERE created_at <= NOW() - INTERVAL '6 minutes' GROUP BY loja
    UNION ALL SELECT loja, 'warning', COUNT(*) FROM waiting
        WHERE created_at <= NOW() - INTERVAL '3 minutes' AND created_at > NOW() - INTERVAL '6 minutes' GROUP BY loja
    UNION ALL SELECT estabelecimento_id, 'warning', COUNT(*) FROM ifood_order_snapshots
        WHERE estabelecimento_id = ANY(@Ids) AND synchronization_state = 'review' AND updated_at_utc > NOW() - INTERVAL '30 days' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'warning', COUNT(*) FROM ifood_events_inbox
        WHERE estabelecimento_id = ANY(@Ids) AND state IN ('queued','processing') AND received_at_utc <= NOW() - INTERVAL '3 minutes' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'critical', COUNT(*) FROM ifood_events_inbox
        WHERE estabelecimento_id = ANY(@Ids) AND state = 'failed' AND received_at_utc > NOW() - INTERVAL '7 days' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'warning', COUNT(*) FROM ifood_events_inbox
        WHERE estabelecimento_id = ANY(@Ids) AND state = 'review' AND received_at_utc > NOW() - INTERVAL '30 days' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'critical', COUNT(*) FROM ifood_outbound_commands
        WHERE estabelecimento_id = ANY(@Ids) AND state IN ('uncertain','review') AND updated_at_utc > NOW() - INTERVAL '7 days' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'critical', COUNT(*) FROM ifood_delivery_completions
        WHERE estabelecimento_id = ANY(@Ids) AND state IN ('uncertain','review') AND created_at_utc > NOW() - INTERVAL '7 days' GROUP BY estabelecimento_id
    UNION ALL SELECT estabelecimento_id, 'critical', COUNT(*) FROM ifood_disputes
        WHERE estabelecimento_id = ANY(@Ids) AND state = 'open' GROUP BY estabelecimento_id)
SELECT i.id AS EstabelecimentoId,
       COALESCE((SELECT SUM(n) FROM alerts a WHERE a.loja = i.id AND a.sev = 'critical'), 0)::int AS Criticos,
       COALESCE((SELECT SUM(n) FROM alerts a WHERE a.loja = i.id AND a.sev = 'warning'), 0)::int AS Avisos
  FROM unnest(@Ids) AS i(id)
""", new { Ids = lojas.ToArray() }, cancellationToken: ct));
            return rows.ToList();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Ambiente sem o schema do iFood (migracoes ainda nao aplicadas): nao ha alertas a mostrar.
            return Array.Empty<OperacaoIfoodRow>();
        }
    }
}
