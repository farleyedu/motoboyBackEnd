-- CRM, Fase 0 (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin):
--   * usuario.telefone_e164: o telefone do usuario passa a ser gravado, sempre em E.164 (+5534991480112).
--   * responsavel_usuario_id em crm_oportunidade e crm_contrato: o responsavel e um usuario da equipe Zippy (super admin), no lugar
--     do enum crm_responsavel (farley/socio). O enum fica, opcional, so para leitura do que ja existia.
--   * Migra o que existe: 'farley' vai para o super admin com "farley" no nome ou e-mail; 'socio' para o primeiro outro super admin
--     (a mesma regra que o fechamento usava para vincular o responsavel a empresa). Sem super admin, fica nulo.
-- Aditiva e idempotente. Sem as tabelas do CRM (bancos de teste), so cria a coluna do usuario.
BEGIN;

ALTER TABLE usuario
    ADD COLUMN IF NOT EXISTS telefone_e164 varchar(16) CHECK (telefone_e164 ~ '^\+[1-9][0-9]{7,14}$');

DO $$
DECLARE
    v_primeiro integer;
    v_farley   integer;
    v_socio    integer;
BEGIN
    IF to_regclass('crm_oportunidade') IS NULL OR to_regclass('crm_contrato') IS NULL THEN
        RETURN;
    END IF;

    ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS responsavel_usuario_id integer REFERENCES usuario(id);
    ALTER TABLE crm_contrato ADD COLUMN IF NOT EXISTS responsavel_usuario_id integer REFERENCES usuario(id);
    ALTER TABLE crm_oportunidade ALTER COLUMN responsavel DROP NOT NULL;
    ALTER TABLE crm_contrato ALTER COLUMN responsavel DROP NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_crm_oportunidade_responsavel_usuario ON crm_oportunidade (responsavel_usuario_id);
    CREATE INDEX IF NOT EXISTS ix_crm_contrato_responsavel_usuario ON crm_contrato (responsavel_usuario_id);

    SELECT id INTO v_primeiro FROM usuario
     WHERE COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL
     ORDER BY created_at, id LIMIT 1;
    SELECT id INTO v_farley FROM usuario
     WHERE COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL
       AND (lower(COALESCE(nome, '')) LIKE '%farley%' OR lower(COALESCE(email, '')) LIKE '%farley%')
     ORDER BY created_at, id LIMIT 1;
    SELECT id INTO v_socio FROM usuario
     WHERE COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL
       AND NOT (lower(COALESCE(nome, '')) LIKE '%farley%' OR lower(COALESCE(email, '')) LIKE '%farley%')
     ORDER BY created_at, id LIMIT 1;
    v_farley := COALESCE(v_farley, v_primeiro);
    v_socio := COALESCE(v_socio, v_primeiro);

    UPDATE crm_oportunidade
       SET responsavel_usuario_id = CASE WHEN responsavel::text = 'farley' THEN v_farley ELSE v_socio END
     WHERE responsavel_usuario_id IS NULL AND responsavel IS NOT NULL;
    UPDATE crm_contrato
       SET responsavel_usuario_id = CASE WHEN responsavel::text = 'farley' THEN v_farley ELSE v_socio END
     WHERE responsavel_usuario_id IS NULL AND responsavel IS NOT NULL;
END $$;

COMMIT;
