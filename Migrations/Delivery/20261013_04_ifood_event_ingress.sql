BEGIN;
-- IF-06: ingresso passivo até habilitação explícita; não autoriza nenhuma loja.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261013_01_ifood_integration_schema') THEN
        RAISE EXCEPTION 'IF-06 requer schema IF-04';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS ifood_polling_state (
    application_key TEXT PRIMARY KEY CHECK (application_key ~ '^[0-9a-f]{64}$'),
    next_poll_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lease_id UUID NULL,
    lease_until_utc TIMESTAMPTZ NULL,
    last_error_code TEXT NULL,
    CHECK ((lease_id IS NULL AND lease_until_utc IS NULL) OR (lease_id IS NOT NULL AND lease_until_utc IS NOT NULL))
);
CREATE TABLE IF NOT EXISTS ifood_event_rejections (
    application_key TEXT NOT NULL REFERENCES ifood_polling_state(application_key),
    payload_hash TEXT NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    payload JSONB NOT NULL,
    error_code TEXT NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (application_key,payload_hash)
);
ALTER TABLE ifood_events_inbox ADD COLUMN IF NOT EXISTS ack_attempts INTEGER NOT NULL DEFAULT 0 CHECK (ack_attempts >= 0);
ALTER TABLE ifood_events_inbox ADD COLUMN IF NOT EXISTS ack_application_key TEXT NULL REFERENCES ifood_polling_state(application_key);
ALTER TABLE ifood_events_inbox ADD COLUMN IF NOT EXISTS ack_next_attempt_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW();
ALTER TABLE ifood_events_inbox ADD COLUMN IF NOT EXISTS ack_last_error_code TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_ifood_inbox_ack_pending ON ifood_events_inbox(ack_next_attempt_at_utc,id) WHERE acknowledged_at_utc IS NULL;
INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261013_04_ifood_event_ingress') ON CONFLICT (version) DO NOTHING;
COMMIT;
