-- Desfaz a Fase 6. Perde o opt-in, o historico de avisos, os tokens de link e os parametros de aviso.
BEGIN;
DROP TABLE IF EXISTS pedido_rastreio_token;
DROP TABLE IF EXISTS pedido_notificacao;
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_notify_minutes;
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_notify_radius;
ALTER TABLE delivery_settings
    DROP COLUMN IF EXISTS notify_dispatch_enabled, DROP COLUMN IF EXISTS notify_arriving_enabled,
    DROP COLUMN IF EXISTS notify_arriving_minutes, DROP COLUMN IF EXISTS notify_arriving_radius_m,
    DROP COLUMN IF EXISTS notify_template_dispatch, DROP COLUMN IF EXISTS notify_template_arriving;
ALTER TABLE motoboy DROP COLUMN IF EXISTS compartilhar_localizacao_cliente;
ALTER TABLE pedido DROP COLUMN IF EXISTS rastreio_opt_in, DROP COLUMN IF EXISTS rastreio_opt_in_em, DROP COLUMN IF EXISTS rastreio_opt_in_origem;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260928_01_avisos_rastreio';
COMMIT;
