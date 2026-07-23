-- Deve retornar zero em todas as consultas de inconsistência.

SELECT COUNT(*) AS aliases_without_valid_canonical
  FROM motoboy alias
  LEFT JOIN motoboy canonical ON canonical.id = alias.canonical_motoboy_id
 WHERE canonical.id IS NULL;

SELECT motoboy_id, COUNT(*) AS open_sessions
  FROM motoboy_active_sessions
 WHERE ended_at_utc IS NULL
   AND revoked_at IS NULL
 GROUP BY motoboy_id
HAVING COUNT(*) > 1;

SELECT s.session_id, s.motoboy_id, s.id_estabelecimento
  FROM motoboy_active_sessions s
  LEFT JOIN motoboy_estabelecimento me
    ON me.motoboy_id = s.motoboy_id
   AND me.estabelecimento_id = s.id_estabelecimento
   AND me.ativo = TRUE
 WHERE s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
   AND me.motoboy_id IS NULL;

SELECT m.id AS simulated_identity_without_enabled_link
  FROM motoboy m
 WHERE m.is_simulated = TRUE
   AND m.canonical_motoboy_id = m.id
   AND NOT EXISTS (
       SELECT 1
         FROM motoboy_estabelecimento me
        WHERE me.motoboy_id = m.id
          AND me.ativo = TRUE
          AND me.simulator_enabled = TRUE
   );

SELECT version, applied_at_utc
  FROM delivery_tracking_schema_versions
 ORDER BY applied_at_utc, version;

-- Relatorio de quarentena: pedidos legados sem estabelecimento. Estes pedidos
-- ja sao excluidos do mapa/fila operacional (toda consulta operacional filtra
-- por id_estabelecimento), mas continuam no banco ate serem corrigidos ou
-- formalmente arquivados. Nao ha correcao automatica: atribuir um
-- estabelecimento errado seria pior do que deixar de fora.
SELECT COUNT(*) AS pedidos_sem_estabelecimento
  FROM pedido
 WHERE id_estabelecimento IS NULL;

SELECT id, data_pedido, status
  FROM pedido
 WHERE id_estabelecimento IS NULL
 ORDER BY data_pedido DESC NULLS LAST, id DESC
 LIMIT 200;

