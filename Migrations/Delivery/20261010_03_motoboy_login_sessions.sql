BEGIN;
CREATE TABLE IF NOT EXISTS motoboy_login_sessions (
    session_id UUID PRIMARY KEY,
    id_usuario INTEGER NOT NULL REFERENCES usuario(id),
    client_instance_id UUID NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at_utc TIMESTAMPTZ NOT NULL,
    revoked_at_utc TIMESTAMPTZ,
    revoke_reason TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_login_sessions_active
    ON motoboy_login_sessions(id_usuario) WHERE revoked_at_utc IS NULL;
ALTER TABLE usuario_refresh_tokens ADD COLUMN IF NOT EXISTS motoboy_login_session_id UUID
    REFERENCES motoboy_login_sessions(session_id);
INSERT INTO delivery_tracking_schema_versions(version)
VALUES ('20261010_03_motoboy_login_sessions') ON CONFLICT DO NOTHING;
COMMIT;
