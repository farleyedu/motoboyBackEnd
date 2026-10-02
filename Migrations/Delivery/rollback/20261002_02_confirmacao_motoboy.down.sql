BEGIN;

-- Antes de reverter, aceite ou recuse as ofertas pendentes e trate os pedidos com status_pedido = 8:
-- sem o status no codigo antigo eles cairiam como "pendente" mantendo a parada na fila.
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_offer_timeout;
ALTER TABLE delivery_settings
    DROP COLUMN IF EXISTS offer_timeout_minutes,
    DROP COLUMN IF EXISTS require_motoboy_acceptance;

DROP INDEX IF EXISTS ix_delivery_route_stops_offered;
ALTER TABLE delivery_route_stops
    DROP COLUMN IF EXISTS offered_at_utc,
    DROP COLUMN IF EXISTS offer_id;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_02_confirmacao_motoboy';

COMMIT;
