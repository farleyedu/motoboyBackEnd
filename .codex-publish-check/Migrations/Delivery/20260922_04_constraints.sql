BEGIN;

-- Estados finais novos da parada: failed (nao entregue), refused (recusado pelo
-- motoboy) e transferred (foi para a fila de outro motoboy).
ALTER TABLE delivery_route_stops DROP CONSTRAINT IF EXISTS ck_delivery_route_stop_status;
ALTER TABLE delivery_route_stops
    ADD CONSTRAINT ck_delivery_route_stop_status
    CHECK (stop_status IN ('assigned', 'en_route', 'completed', 'canceled', 'removed', 'failed', 'refused', 'transferred'));

ALTER TABLE delivery_route_stops DROP CONSTRAINT IF EXISTS ck_delivery_route_stop_completed_by;
ALTER TABLE delivery_route_stops
    ADD CONSTRAINT ck_delivery_route_stop_completed_by
    CHECK (completed_by IS NULL OR completed_by IN ('motoboy', 'operator'));

-- Posicao unica por FILA (motoboy + estabelecimento), nao mais por motoboy global.
DROP INDEX IF EXISTS ux_delivery_route_stop_position_active;
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_route_stop_position_active_v2
    ON delivery_route_stops (motoboy_id, estabelecimento_id, position)
    WHERE stop_status IN ('assigned', 'en_route');

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_transfer_policy;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_transfer_policy
    CHECK (transfer_policy IN ('direct', 'establishment_approval'));

ALTER TABLE delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_status;
ALTER TABLE delivery_transfer_requests
    ADD CONSTRAINT ck_delivery_transfer_status
    CHECK (status IN ('pending_approval', 'completed', 'rejected', 'cancelled'));

ALTER TABLE delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_policy;
ALTER TABLE delivery_transfer_requests
    ADD CONSTRAINT ck_delivery_transfer_policy
    CHECK (policy IN ('direct', 'establishment_approval', 'operator'));

ALTER TABLE delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_requested_by;
ALTER TABLE delivery_transfer_requests
    ADD CONSTRAINT ck_delivery_transfer_requested_by
    CHECK (requested_by IN ('motoboy', 'operator'));

ALTER TABLE delivery_transfer_requests DROP CONSTRAINT IF EXISTS ck_delivery_transfer_distinct_motoboys;
ALTER TABLE delivery_transfer_requests
    ADD CONSTRAINT ck_delivery_transfer_distinct_motoboys
    CHECK (from_motoboy_id <> to_motoboy_id);

-- No maximo uma solicitacao aguardando aprovacao por pedido.
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_transfer_pending_per_pedido
    ON delivery_transfer_requests (pedido_id)
    WHERE status = 'pending_approval';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260922_04_constraints')
ON CONFLICT (version) DO NOTHING;

COMMIT;
