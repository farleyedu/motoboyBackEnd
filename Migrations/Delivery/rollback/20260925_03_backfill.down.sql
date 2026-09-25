BEGIN;

-- Desfaz 20260925_03_backfill.sql: pedidos do iFood voltam ao padrao (origem 'atendente', sem referencia).
-- Rode depois de 20260925_05_constraints.down.sql (senao o CHECK/indice ainda existem, o que nao atrapalha).
UPDATE pedido
   SET origem = 'atendente',
       origem_ref = NULL
 WHERE origem = 'ifood'
   AND origem_ref IS NOT DISTINCT FROM id_ifood;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260925_03_backfill';

COMMIT;
