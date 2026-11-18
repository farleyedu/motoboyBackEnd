BEGIN;

-- Apaga o historico de uso e custo do assistente (nao volta).
DROP TABLE IF EXISTS assistente_eventos;
DROP TABLE IF EXISTS assistente_uso;
ALTER TABLE IF EXISTS assistente_config DROP COLUMN IF EXISTS precos;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_05_assistente_uso';

COMMIT;
