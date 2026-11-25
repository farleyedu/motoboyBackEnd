BEGIN;

CREATE OR REPLACE FUNCTION set_motoboy_canonical_id()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.canonical_motoboy_id IS NULL THEN
        NEW.canonical_motoboy_id := NEW.id;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_motoboy_canonical_id ON motoboy;
CREATE TRIGGER trg_motoboy_canonical_id
BEFORE INSERT ON motoboy
FOR EACH ROW EXECUTE FUNCTION set_motoboy_canonical_id();

ALTER TABLE motoboy
    ALTER COLUMN canonical_motoboy_id SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_motoboy_canonical') THEN
        ALTER TABLE motoboy
            ADD CONSTRAINT fk_motoboy_canonical
            FOREIGN KEY (canonical_motoboy_id) REFERENCES motoboy(id) NOT VALID;
    END IF;
END $$;

ALTER TABLE motoboy VALIDATE CONSTRAINT fk_motoboy_canonical;

ALTER TABLE motoboy_active_sessions
    ALTER COLUMN session_epoch SET NOT NULL,
    ALTER COLUMN origin SET NOT NULL,
    ALTER COLUMN started_at_utc SET NOT NULL,
    ALTER COLUMN last_heartbeat_at_utc SET NOT NULL,
    ALTER COLUMN expires_at_utc SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_canonical_user
    ON motoboy (id_usuario)
    WHERE id_usuario IS NOT NULL AND canonical_motoboy_id = id;

CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_one_open_session
    ON motoboy_active_sessions (motoboy_id)
    WHERE ended_at_utc IS NULL AND revoked_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_session_idempotency
    ON motoboy_active_sessions (started_by_user_id, origin, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_session_origin') THEN
        ALTER TABLE motoboy_active_sessions
            ADD CONSTRAINT ck_motoboy_session_origin CHECK (origin IN ('mobile', 'simulator'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_session_expiration') THEN
        ALTER TABLE motoboy_active_sessions
            ADD CONSTRAINT ck_motoboy_session_expiration CHECK (expires_at_utc >= started_at_utc);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_location_coordinates') THEN
        ALTER TABLE motoboy_location_samples
            ADD CONSTRAINT ck_motoboy_location_coordinates
            CHECK (latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_location_sequence') THEN
        ALTER TABLE motoboy_location_samples
            ADD CONSTRAINT ck_motoboy_location_sequence CHECK (sequence > 0);
    END IF;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260722_04_constraints')
ON CONFLICT (version) DO NOTHING;

COMMIT;

