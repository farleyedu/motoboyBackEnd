BEGIN;

-- Preenche pedido.id_estabelecimento para pedidos legados que ja tem motoboy
-- responsavel, usando o vinculo ativo (canonical) desse motoboy -- so quando
-- existe exatamente um vinculo ativo. Pedidos ambiguos (motoboy com varios
-- vinculos) ou sem motoboy responsavel ficam de fora e continuam aparecendo
-- no relatorio de quarentena (20260722_05_verify.sql / 20260723_01_preflight.sql);
-- nao adivinhamos estabelecimento sem base solida.
WITH candidate_links AS (
    SELECT p.id AS pedido_id, me.estabelecimento_id
      FROM pedido p
      JOIN motoboy m ON m.id = p.motoboy_responsavel
      JOIN motoboy_estabelecimento me ON me.motoboy_id = m.canonical_motoboy_id AND me.ativo = TRUE
     WHERE p.id_estabelecimento IS NULL
),
unambiguous AS (
    -- O HAVING abaixo ja garante um unico estabelecimento distinto por pedido, entao
    -- qualquer elemento serve. Usamos array_agg em vez de MIN() porque o agregado
    -- min(uuid) so existe a partir do PostgreSQL 17 -- em versoes anteriores a query
    -- falha com 42883 e aborta a transacao inteira da migration.
    SELECT pedido_id, (array_agg(DISTINCT estabelecimento_id))[1] AS estabelecimento_id
      FROM candidate_links
     GROUP BY pedido_id
    HAVING COUNT(DISTINCT estabelecimento_id) = 1
)
UPDATE pedido p
   SET id_estabelecimento = u.estabelecimento_id
  FROM unambiguous u
 WHERE p.id = u.pedido_id;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260723_03_backfill')
ON CONFLICT (version) DO NOTHING;

COMMIT;
