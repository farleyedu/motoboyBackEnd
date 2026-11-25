-- Desfaz a Fase 4. Perde os locks e o estado de retorno a loja (a fila volta a se comportar como antes).
BEGIN;
DROP INDEX IF EXISTS ix_delivery_route_stops_locked;
ALTER TABLE delivery_route_stops DROP COLUMN IF EXISTS locked, DROP COLUMN IF EXISTS locked_by_user_id, DROP COLUMN IF EXISTS locked_at_utc;
ALTER TABLE delivery_motoboy_route DROP CONSTRAINT IF EXISTS ck_delivery_motoboy_route_state;
ALTER TABLE delivery_motoboy_route DROP COLUMN IF EXISTS route_state, DROP COLUMN IF EXISTS returning_since_utc;
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_store_return_radius;
ALTER TABLE delivery_settings DROP COLUMN IF EXISTS require_return_to_store, DROP COLUMN IF EXISTS store_return_radius_m, DROP COLUMN IF EXISTS auto_finish_route_on_return;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260927_01_regras_de_rota';
COMMIT;
