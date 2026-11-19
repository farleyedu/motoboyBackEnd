BEGIN;

-- Apaga as chaves cadastradas pela Gestao (as variaveis de ambiente continuam valendo) e a auditoria delas.
DROP TABLE IF EXISTS assistente_chaves_auditoria;
DROP TABLE IF EXISTS assistente_chaves;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_11_assistente_chaves';

COMMIT;
