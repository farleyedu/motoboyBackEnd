BEGIN;

-- Volta a recusar consumo no local por regra fixa. Nenhum pedido ja importado e apagado ou alterado.
ALTER TABLE ifood_app_settings DROP COLUMN IF EXISTS dine_in_enabled;

DELETE FROM delivery_tracking_schema_versions WHERE version='20261018_01_ifood_dine_in';
COMMIT;
