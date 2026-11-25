BEGIN;

DROP INDEX IF EXISTS ix_conversas_canal;
ALTER TABLE conversas DROP COLUMN IF EXISTS id_canal;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_11_conversas_canal';

COMMIT;
