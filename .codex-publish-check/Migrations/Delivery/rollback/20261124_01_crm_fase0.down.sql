-- Desfaz 20261124_01_crm_fase0.sql. O enum crm_responsavel continua com o valor antigo de cada registro; quem foi criado depois
-- (so com responsavel_usuario_id) volta sem responsavel e o NOT NULL do enum nao e restaurado.
BEGIN;
DROP INDEX IF EXISTS ix_crm_contrato_responsavel_usuario;
DROP INDEX IF EXISTS ix_crm_oportunidade_responsavel_usuario;
ALTER TABLE IF EXISTS crm_contrato DROP COLUMN IF EXISTS responsavel_usuario_id;
ALTER TABLE IF EXISTS crm_oportunidade DROP COLUMN IF EXISTS responsavel_usuario_id;
ALTER TABLE usuario DROP COLUMN IF EXISTS telefone_e164;
COMMIT;
