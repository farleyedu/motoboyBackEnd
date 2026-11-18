BEGIN;

-- Recusa se o salao ja registrou algo: apagar perderia auditoria.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM mesa_evento) OR EXISTS (SELECT 1 FROM mesas_config_historico) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem eventos ou historico de configuracao do Mesas.';
    END IF;
END $$;

DROP TABLE IF EXISTS mesa_evento;
DROP TABLE IF EXISTS mesa_comando;
DROP TABLE IF EXISTS mesas_config_historico;
DROP TABLE IF EXISTS mesas_config;
DROP INDEX IF EXISTS ix_gestao_pessoas_usuario;
DROP INDEX IF EXISTS ix_gestao_pessoas_funcao_codigo;
ALTER TABLE gestao_pessoas DROP CONSTRAINT IF EXISTS ck_gestao_pessoas_funcao_codigo;
ALTER TABLE gestao_pessoas DROP COLUMN IF EXISTS funcao_codigo;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261203_03_mesas_nucleo';

COMMIT;
