BEGIN;

-- Rodar depois de 20260922_04_constraints.down.sql.
-- A chave antiga (so motoboy_id) so volta se cada motoboy tiver um unico
-- cabecalho de rota; com filas em dois estabelecimentos, mantem o mais recente.
DELETE FROM delivery_motoboy_route r
 USING delivery_motoboy_route newer
 WHERE newer.motoboy_id = r.motoboy_id
   AND newer.updated_at_utc > r.updated_at_utc;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'delivery_motoboy_route_pkey'
           AND conrelid = 'delivery_motoboy_route'::regclass
           AND array_length(conkey, 1) = 2
    ) THEN
        ALTER TABLE delivery_motoboy_route DROP CONSTRAINT delivery_motoboy_route_pkey;
        ALTER TABLE delivery_motoboy_route ADD CONSTRAINT delivery_motoboy_route_pkey PRIMARY KEY (motoboy_id);
    END IF;
END $$;

DROP INDEX IF EXISTS ix_motoboy_location_samples_trajectory;
DROP INDEX IF EXISTS ix_delivery_route_stops_completed;
DROP INDEX IF EXISTS ix_delivery_route_stops_queue;

DROP TABLE IF EXISTS delivery_transfer_requests;
DROP TABLE IF EXISTS delivery_settings;

ALTER TABLE delivery_route_stops
    DROP COLUMN IF EXISTS completed_by,
    DROP COLUMN IF EXISTS transfer_request_id,
    DROP COLUMN IF EXISTS transferred_at_utc,
    DROP COLUMN IF EXISTS refusal_reason,
    DROP COLUMN IF EXISTS refused_at_utc,
    DROP COLUMN IF EXISTS failure_reason,
    DROP COLUMN IF EXISTS failed_at_utc,
    DROP COLUMN IF EXISTS arrived_at_utc,
    DROP COLUMN IF EXISTS picked_up_at_utc;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260922_02_schema';

COMMIT;
