BEGIN;

-- Desfaz 20261104_01_presenca_7h_incidentes. Os incidentes sao historico operacional: so remova se tiver certeza.
-- Turnos mobile abertos mantem expires_at_utc (agora uma data real); a versao anterior do servidor volta a renova-la por heartbeat.
DROP TABLE IF EXISTS delivery_motoboy_disconnect_incident_actions;
DROP TABLE IF EXISTS delivery_motoboy_disconnect_incident_items;
DROP TABLE IF EXISTS delivery_motoboy_disconnect_incidents;
ALTER TABLE motoboy_active_sessions DROP COLUMN IF EXISTS last_human_activity_at_utc;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261104_01_presenca_7h_incidentes';

COMMIT;
