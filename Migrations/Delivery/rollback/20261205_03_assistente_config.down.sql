BEGIN;

-- Volta para a IA padrao do codigo (anthropic, claude-opus-5-5). O historico de quem mudou a configuracao se perde.
DROP TABLE IF EXISTS assistente_config_historico;
DROP TABLE IF EXISTS assistente_config;
ALTER TABLE IF EXISTS assistente_mensagens DROP COLUMN IF EXISTS usou_reserva;
ALTER TABLE IF EXISTS assistente_mensagens DROP COLUMN IF EXISTS provedor;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_03_assistente_config';

COMMIT;
