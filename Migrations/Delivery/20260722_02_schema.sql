BEGIN;

CREATE TABLE IF NOT EXISTS delivery_tracking_schema_versions (
    version TEXT PRIMARY KEY,
    applied_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE SEQUENCE IF NOT EXISTS motoboy_session_epoch_seq AS BIGINT START WITH 1;

ALTER TABLE motoboy
    ADD COLUMN IF NOT EXISTS id_usuario INTEGER NULL,
    ADD COLUMN IF NOT EXISTS id_estabelecimento UUID NULL,
    ADD COLUMN IF NOT EXISTS canonical_motoboy_id INTEGER NULL,
    ADD COLUMN IF NOT EXISTS is_simulated BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE pedido
    ADD COLUMN IF NOT EXISTS id_estabelecimento UUID NULL;

-- Estruturas legadas mantidas somente para leitura/rollback durante o cutover.
CREATE TABLE IF NOT EXISTS motoboy_location_state (
    motoboy_id INTEGER PRIMARY KEY,
    id_estabelecimento UUID NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    last_sequence BIGINT NULL,
    client_timestamp_utc TIMESTAMPTZ NOT NULL,
    server_received_at_utc TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS motoboy_location_history_daily (
    id BIGSERIAL PRIMARY KEY,
    motoboy_id INTEGER NOT NULL,
    id_estabelecimento UUID NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    client_timestamp_utc TIMESTAMPTZ NOT NULL,
    server_received_at_utc TIMESTAMPTZ NOT NULL,
    local_date DATE NOT NULL,
    sequence BIGINT NULL
);

CREATE TABLE IF NOT EXISTS motoboy_estabelecimento (
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    simulator_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    disabled_at_utc TIMESTAMPTZ NULL,
    PRIMARY KEY (motoboy_id, estabelecimento_id)
);

CREATE TABLE IF NOT EXISTS motoboy_active_sessions (
    session_id UUID PRIMARY KEY,
    motoboy_id INTEGER NOT NULL,
    id_usuario INTEGER NULL,
    id_estabelecimento UUID NOT NULL,
    device_type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at TIMESTAMPTZ NULL,
    revoke_reason TEXT NULL
);

ALTER TABLE motoboy_active_sessions
    ADD COLUMN IF NOT EXISTS session_epoch BIGINT NULL,
    ADD COLUMN IF NOT EXISTS contract_version INTEGER NOT NULL DEFAULT 2,
    ADD COLUMN IF NOT EXISTS origin TEXT NULL,
    ADD COLUMN IF NOT EXISTS client_instance_id TEXT NULL,
    ADD COLUMN IF NOT EXISTS started_by_user_id INTEGER NULL,
    ADD COLUMN IF NOT EXISTS idempotency_key UUID NULL,
    ADD COLUMN IF NOT EXISTS started_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS last_heartbeat_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS expires_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS ended_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS end_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS motoboy_operational_session_events (
    event_id UUID PRIMARY KEY,
    session_id UUID NOT NULL REFERENCES motoboy_active_sessions(session_id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    event_type TEXT NOT NULL,
    actor_user_id INTEGER NULL,
    reason TEXT NULL,
    details JSONB NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS motoboy_location_samples (
    id BIGSERIAL PRIMARY KEY,
    sample_id UUID NOT NULL,
    session_id UUID NOT NULL REFERENCES motoboy_active_sessions(session_id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    sequence BIGINT NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    captured_at_utc TIMESTAMPTZ NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    payload_hash TEXT NOT NULL,
    updated_current BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (session_id, sample_id),
    UNIQUE (session_id, sequence)
);

CREATE TABLE IF NOT EXISTS motoboy_location_current (
    session_id UUID PRIMARY KEY REFERENCES motoboy_active_sessions(session_id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    sample_id UUID NOT NULL,
    sequence BIGINT NOT NULL,
    latitude DOUBLE PRECISION NOT NULL,
    longitude DOUBLE PRECISION NOT NULL,
    accuracy_meters DOUBLE PRECISION NULL,
    speed_mps DOUBLE PRECISION NULL,
    heading_degrees DOUBLE PRECISION NULL,
    tracking_mode TEXT NOT NULL DEFAULT 'online_idle',
    quality TEXT NOT NULL DEFAULT 'unknown',
    captured_at_utc TIMESTAMPTZ NOT NULL,
    received_at_utc TIMESTAMPTZ NOT NULL,
    version BIGINT NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS delivery_realtime_outbox (
    event_id UUID PRIMARY KEY,
    event_name TEXT NOT NULL,
    target_group TEXT NOT NULL,
    estabelecimento_id UUID NOT NULL,
    motoboy_id INTEGER NULL,
    session_id UUID NULL,
    session_epoch BIGINT NULL,
    aggregate_version BIGINT NOT NULL,
    payload JSONB NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at_utc TIMESTAMPTZ NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    next_attempt_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_motoboy_estabelecimento_lookup
    ON motoboy_estabelecimento (estabelecimento_id, ativo, simulator_enabled, created_at_utc, motoboy_id);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_state_estabelecimento
    ON motoboy_location_state (id_estabelecimento);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_history_daily_lookup
    ON motoboy_location_history_daily (id_estabelecimento, motoboy_id, local_date, client_timestamp_utc);

CREATE INDEX IF NOT EXISTS ix_pedido_id_estabelecimento
    ON pedido (id_estabelecimento);

CREATE INDEX IF NOT EXISTS ix_motoboy_active_sessions_expiration
    ON motoboy_active_sessions (expires_at_utc)
    WHERE ended_at_utc IS NULL AND revoked_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_motoboy_session_events_session
    ON motoboy_operational_session_events (session_id, occurred_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_samples_lookup
    ON motoboy_location_samples (estabelecimento_id, motoboy_id, received_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_motoboy_location_current_establishment
    ON motoboy_location_current (estabelecimento_id, received_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_delivery_realtime_outbox_pending
    ON delivery_realtime_outbox (next_attempt_at_utc, occurred_at_utc)
    WHERE published_at_utc IS NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260722_02_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
