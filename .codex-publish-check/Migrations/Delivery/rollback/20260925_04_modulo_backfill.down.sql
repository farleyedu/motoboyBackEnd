BEGIN;

-- Desfaz 20260925_04_modulo_backfill.sql: retira PEDIDOS dos estabelecimentos.
-- ATENCAO: remove tambem PEDIDOS de quem o recebeu por escolha manual depois do backfill.
-- Se isso importar, rode antes o SELECT abaixo e guarde o resultado.
--   SELECT id, nome_fantasia FROM estabelecimentos WHERE modulos_ativos @> ARRAY['PEDIDOS']::modulo_enum[];
UPDATE estabelecimentos
   SET modulos_ativos = array_remove(modulos_ativos, 'PEDIDOS'::modulo_enum)
 WHERE modulos_ativos @> ARRAY['PEDIDOS']::modulo_enum[];

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260925_04_modulo_backfill';

COMMIT;
