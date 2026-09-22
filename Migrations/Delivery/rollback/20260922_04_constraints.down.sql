BEGIN;

-- ATENCAO: paradas em 'failed', 'refused' ou 'transferred' violam o CHECK antigo.
-- Elas sao historico (nao ativas); a restauracao abaixo as reclassifica como
-- 'removed' para o CHECK anterior poder voltar.
UPDATE delivery_route_stops
   SET stop_status = 'removed', updated_at_utc = NOW()
 WHERE stop_status IN ('failed', 'refused', 'transferred');

ALTER TABLE delivery_route_stops DROP CONSTRAINT IF EXISTS ck_delivery_route_stop_status;
ALTER TABLE delivery_route_stops
    ADD CONSTRAINT ck_delivery_route_stop_status
    CHECK (stop_status IN ('assigned', 'en_route', 'completed', 'canceled', 'removed'));

ALTER TABLE delivery_route_stops DROP CONSTRAINT IF EXISTS ck_delivery_route_stop_completed_by;

-- O indice global por motoboy so volta se nenhum motoboy tiver filas ativas em
-- dois estabelecimentos com posicoes repetidas.
DROP INDEX IF EXISTS ux_delivery_route_stop_position_active_v2;
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_route_stop_position_active
    ON delivery_route_stops (motoboy_id, position)
    WHERE stop_status IN ('assigned', 'en_route');

DROP INDEX IF EXISTS ux_delivery_transfer_pending_per_pedido;
ALTER TABLE IF EXISTS delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_status;
ALTER TABLE IF EXISTS delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_policy;
ALTER TABLE IF EXISTS delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_requested_by;
ALTER TABLE IF EXISTS delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_distinct_motoboys;
ALTER TABLE IF EXISTS delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_transfer_policy;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260922_04_constraints';

COMMIT;
