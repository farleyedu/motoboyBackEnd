BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_location_sequence') THEN
        ALTER TABLE motoboy_location_samples DROP CONSTRAINT ck_motoboy_location_sequence;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_location_coordinates') THEN
        ALTER TABLE motoboy_location_samples DROP CONSTRAINT ck_motoboy_location_coordinates;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_session_expiration') THEN
        ALTER TABLE motoboy_active_sessions DROP CONSTRAINT ck_motoboy_session_expiration;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_session_origin') THEN
        ALTER TABLE motoboy_active_sessions DROP CONSTRAINT ck_motoboy_session_origin;
    END IF;
END $$;

DROP INDEX IF EXISTS ux_motoboy_session_idempotency;
DROP INDEX IF EXISTS ux_motoboy_one_open_session;
DROP INDEX IF EXISTS ux_motoboy_canonical_user;

ALTER TABLE motoboy_active_sessions
    ALTER COLUMN expires_at_utc DROP NOT NULL,
    ALTER COLUMN last_heartbeat_at_utc DROP NOT NULL,
    ALTER COLUMN started_at_utc DROP NOT NULL,
    ALTER COLUMN origin DROP NOT NULL,
    ALTER COLUMN session_epoch DROP NOT NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_motoboy_canonical') THEN
        ALTER TABLE motoboy DROP CONSTRAINT fk_motoboy_canonical;
    END IF;
END $$;

ALTER TABLE motoboy
    ALTER COLUMN canonical_motoboy_id DROP NOT NULL;

DROP TRIGGER IF EXISTS trg_motoboy_canonical_id ON motoboy;
DROP FUNCTION IF EXISTS set_motoboy_canonical_id();

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260722_04_constraints';

COMMIT;
