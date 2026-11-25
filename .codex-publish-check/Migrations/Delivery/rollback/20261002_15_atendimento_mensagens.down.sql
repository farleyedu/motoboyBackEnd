BEGIN;

ALTER TABLE estabelecimento_atendimento_config DROP COLUMN IF EXISTS mensagens;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_15_atendimento_mensagens';

COMMIT;
