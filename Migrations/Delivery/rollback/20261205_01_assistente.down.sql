BEGIN;

-- Recusa se ja existe acao auditada: apagar perderia o registro de quem mudou o que.
DO $$
BEGIN
    IF to_regclass('assistente_auditoria') IS NOT NULL AND EXISTS (SELECT 1 FROM assistente_auditoria) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem acoes do assistente auditadas.';
    END IF;
END $$;

DROP TABLE IF EXISTS assistente_auditoria;
DROP TABLE IF EXISTS assistente_acoes;
DROP TABLE IF EXISTS assistente_mensagens;
DROP TABLE IF EXISTS assistente_conversas;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_01_assistente';

COMMIT;
