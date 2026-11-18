BEGIN;

-- Esquece o que os provedores mostraram nao aceitar (o backend reaprende na proxima recusa).
DROP INDEX IF EXISTS ix_assistente_eventos_cadeia_esgotada;
DROP TABLE IF EXISTS assistente_provedor_nao_suporta;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_09_assistente_nao_suporta';

COMMIT;
