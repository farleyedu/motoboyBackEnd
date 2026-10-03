BEGIN;

ALTER TABLE conversas DROP COLUMN IF EXISTS fluxo_versao;
ALTER TABLE conversas DROP COLUMN IF EXISTS fluxo_chave;
ALTER TABLE conversas DROP COLUMN IF EXISTS fluxo_estado;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_13_conversas_fluxo';

COMMIT;
