BEGIN;
-- Apenas turnos ainda abertos; encerramentos e revogações nunca são desfeitos.
UPDATE motoboy_active_sessions SET expires_at_utc=TIMESTAMPTZ '9999-12-31 00:00:00+00'
 WHERE origin='mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL;
UPDATE motoboy_login_sessions SET expires_at_utc=TIMESTAMPTZ '9999-12-31 00:00:00+00'
 WHERE revoked_at_utc IS NULL;
UPDATE usuario_refresh_tokens rt SET expires_at=TIMESTAMPTZ '9999-12-31 00:00:00+00'
 FROM motoboy_login_sessions ls
 WHERE rt.motoboy_login_session_id=ls.session_id AND rt.revoked_at IS NULL AND ls.revoked_at_utc IS NULL;
INSERT INTO delivery_tracking_schema_versions(version)
 VALUES ('20261011_01_disponibilidade_persistente') ON CONFLICT DO NOTHING;
COMMIT;
