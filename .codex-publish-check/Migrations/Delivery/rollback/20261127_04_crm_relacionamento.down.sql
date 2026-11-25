-- Desfaz 20261127_04_crm_relacionamento.sql. Apaga a trilha de relacionamento (responsavel, proximo passo, marcacoes, retencoes,
-- avisos); os leads criados continuam, sem origem, decisor e whatsapp_e164.
BEGIN;
DROP TABLE IF EXISTS acompanhamento_relacionamento_evento;
DROP TABLE IF EXISTS acompanhamento_retencao;
DROP TABLE IF EXISTS acompanhamento_rel_aviso;
DROP TABLE IF EXISTS acompanhamento_cobranca_responsavel;
DROP TABLE IF EXISTS acompanhamento_marcacao;
DROP TABLE IF EXISTS acompanhamento_contato_marco;
DROP TABLE IF EXISTS acompanhamento_proximo_passo;
DROP TABLE IF EXISTS acompanhamento_relacionamento;
DROP INDEX IF EXISTS ux_crm_oportunidade_lead_chave;
DROP INDEX IF EXISTS ix_crm_oportunidade_nome_normalizado;
DROP INDEX IF EXISTS ix_crm_oportunidade_whatsapp;
ALTER TABLE IF EXISTS crm_oportunidade DROP CONSTRAINT IF EXISTS ck_crm_oportunidade_whatsapp_e164;
ALTER TABLE IF EXISTS crm_oportunidade
    DROP COLUMN IF EXISTS lead_hash, DROP COLUMN IF EXISTS lead_chave, DROP COLUMN IF EXISTS nome_normalizado,
    DROP COLUMN IF EXISTS whatsapp_e164, DROP COLUMN IF EXISTS decisor, DROP COLUMN IF EXISTS origem;
COMMIT;
