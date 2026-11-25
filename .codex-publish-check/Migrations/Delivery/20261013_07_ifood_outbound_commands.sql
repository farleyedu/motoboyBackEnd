BEGIN;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261013_06_ifood_order_projection') THEN
        RAISE EXCEPTION 'IF-09 requer IF-08';
    END IF;
END $$;
ALTER TABLE ifood_outbound_commands ADD COLUMN IF NOT EXISTS send_started_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS http_accepted_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS confirmed_event_id UUID NULL,
    ADD COLUMN IF NOT EXISTS not_before_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS deadline_at_utc TIMESTAMPTZ NULL;
-- Revisão também bloqueia nova intenção: não contornar resultado desconhecido com novo operationId.
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_active_food_command ON ifood_outbound_commands(merchant_id,external_order_id)
WHERE command_type IN('confirm','start_preparation','ready_to_pickup','dispatch') AND state IN('queued','processing','uncertain','review');
CREATE TABLE IF NOT EXISTS ifood_command_attempts (
    id BIGSERIAL PRIMARY KEY,operation_id UUID NOT NULL REFERENCES ifood_outbound_commands(operation_id),
    attempt_number INTEGER NOT NULL CHECK(attempt_number>=0),
    stage TEXT NOT NULL CHECK(stage IN('claimed','sending','accepted','uncertain','retry','rejected','review','succeeded')),
    code TEXT NULL,http_status INTEGER NULL CHECK(http_status IS NULL OR http_status BETWEEN 100 AND 599),
    occurred_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(operation_id,attempt_number,stage)
);
INSERT INTO delivery_tracking_schema_versions(version)VALUES('20261013_07_ifood_outbound_commands') ON CONFLICT DO NOTHING;
COMMIT;
