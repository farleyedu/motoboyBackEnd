BEGIN;

-- Esquece a ultima falha explicada de cada item (o estado da cadeia continua).
ALTER TABLE assistente_cadeia_estado DROP COLUMN IF EXISTS falha_em;
ALTER TABLE assistente_cadeia_estado DROP COLUMN IF EXISTS falha;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261209_01_assistente_falha';

COMMIT;
