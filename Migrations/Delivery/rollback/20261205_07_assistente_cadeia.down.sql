BEGIN;

-- Volta para principal + reserva. A cadeia salva e o estado dos itens se perdem; eventos "pulo" sao apagados.
DELETE FROM assistente_eventos WHERE tipo = 'pulo';
ALTER TABLE assistente_eventos DROP CONSTRAINT IF EXISTS ck_assistente_eventos_tipo;
ALTER TABLE assistente_eventos ADD CONSTRAINT ck_assistente_eventos_tipo
    CHECK (tipo IN ('erro', 'limite', 'chamado_enviado', 'sem_funcionalidade'));
DROP INDEX IF EXISTS ix_assistente_uso_item;
ALTER TABLE IF EXISTS assistente_uso DROP COLUMN IF EXISTS item_id;
DROP TABLE IF EXISTS assistente_cadeia_estado;
-- Primeiro item da cadeia volta a ser o principal; se nao for anthropic/openai, volta para o padrao.
UPDATE assistente_config SET provedor = 'anthropic', modelo = 'claude-opus-5-5' WHERE provedor NOT IN ('anthropic', 'openai');
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_modo;
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_pagos;
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_provedor;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_provedor CHECK (
    provedor IN ('anthropic', 'openai') AND (reserva_provedor IS NULL OR reserva_provedor IN ('anthropic', 'openai')));
ALTER TABLE assistente_config DROP COLUMN IF EXISTS pagos;
ALTER TABLE assistente_config DROP COLUMN IF EXISTS modo;
ALTER TABLE assistente_config DROP COLUMN IF EXISTS provedores;
ALTER TABLE assistente_config DROP COLUMN IF EXISTS cadeia;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_07_assistente_cadeia';

COMMIT;
