-- Conferencia da 20261104_01 (somente leitura). Cada linha deve sair com ok = true.
SELECT 'last_human_activity_at_utc' AS item, EXISTS (
    SELECT 1 FROM information_schema.columns
     WHERE table_schema = current_schema() AND table_name = 'motoboy_active_sessions'
       AND column_name = 'last_human_activity_at_utc') AS ok
UNION ALL SELECT 'incidentes', to_regclass('delivery_motoboy_disconnect_incidents') IS NOT NULL
UNION ALL SELECT 'itens', to_regclass('delivery_motoboy_disconnect_incident_items') IS NOT NULL
UNION ALL SELECT 'acoes', to_regclass('delivery_motoboy_disconnect_incident_actions') IS NOT NULL
UNION ALL SELECT 'turnos mobile abertos sem atividade', NOT EXISTS (
    SELECT 1 FROM motoboy_active_sessions
     WHERE origin = 'mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL AND last_human_activity_at_utc IS NULL);
