-- Separa presença recente do turno aberto, sem alterar credenciais ou GPS.
UPDATE motoboy_active_sessions
SET expires_at_utc = COALESCE(last_seen_at, last_heartbeat_at_utc, started_at_utc)
    + INTERVAL '12 minutes'
WHERE origin = 'mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL
  AND expires_at_utc >= TIMESTAMPTZ '9000-01-01 00:00:00+00';
