BEGIN;

-- Antes de reverter, reabra (ou trate) os pedidos com status_pedido = 7: sem o status no
-- codigo antigo eles cairiam como "pendente" sem motoboy.
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_encerramento_horas;
ALTER TABLE delivery_settings
    DROP COLUMN IF EXISTS encerramento_auto_horas,
    DROP COLUMN IF EXISTS encerramento_auto_ativo;

DROP TABLE IF EXISTS pedido_encerramento;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_01_encerramento_automatico';

COMMIT;
