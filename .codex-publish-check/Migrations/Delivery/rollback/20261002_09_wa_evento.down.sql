BEGIN;

-- Reverte a fila de eventos do webhook. Eventos ainda pendentes se perdem: rode so com o webhook parado.
DROP TABLE IF EXISTS wa_evento;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_09_wa_evento';

COMMIT;
