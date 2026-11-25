BEGIN;

-- ---------------------------------------------------------------------------
-- Estabelecimentos que ja usam Delivery ou Cardapio Web ganham o modulo PEDIDOS (dependencia:
-- DELIVERY e CARDAPIOWEB exigem PEDIDOS). Arquivo separado de 20260925_01 porque o valor
-- de enum criado la so pode ser usado depois de confirmado.
-- So acrescenta o modulo; nunca remove nem reordena os existentes. Idempotente.
-- ---------------------------------------------------------------------------

UPDATE estabelecimentos
   SET modulos_ativos = array_append(modulos_ativos, 'PEDIDOS'::modulo_enum)
 WHERE (modulos_ativos @> ARRAY['DELIVERY']::modulo_enum[]
        OR modulos_ativos @> ARRAY['CARDAPIOWEB']::modulo_enum[])
   AND NOT (modulos_ativos @> ARRAY['PEDIDOS']::modulo_enum[]);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260925_04_modulo_backfill')
ON CONFLICT (version) DO NOTHING;

COMMIT;
