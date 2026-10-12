-- Usar junto com a reversão do código da janela de presença.
UPDATE motoboy_active_sessions SET expires_at_utc = TIMESTAMPTZ '9999-12-31 00:00:00+00'
WHERE origin = 'mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL;
