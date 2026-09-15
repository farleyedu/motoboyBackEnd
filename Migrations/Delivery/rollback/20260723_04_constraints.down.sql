BEGIN;

DROP INDEX IF EXISTS ux_delivery_route_stop_en_route_per_motoboy;
DROP INDEX IF EXISTS ux_delivery_route_stop_pedido_active;
DROP INDEX IF EXISTS ux_delivery_route_stop_position_active;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_route_stop_position') THEN
        ALTER TABLE delivery_route_stops DROP CONSTRAINT ck_delivery_route_stop_position;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_route_stop_status') THEN
        ALTER TABLE delivery_route_stops DROP CONSTRAINT ck_delivery_route_stop_status;
    END IF;
END $$;

DROP INDEX IF EXISTS ux_pedido_id_ifood;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260723_04_constraints';

COMMIT;
