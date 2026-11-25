-- Desfaz 20261127_15_assistente_audio.sql. Reverta ANTES 20261127_16 (crm_objecao referencia crm_assistente_entrada; o DROP falha se nao).
-- Apaga consentimentos, entradas, razao de custo e auditoria do assistente. Por padrao so roda com as tabelas vazias; com dados exige
-- confirmacao explicita. Nao apaga historico do CRM nem oportunidades: o audio em si nunca foi gravado.
BEGIN;

-- Trava: recusa se alguma tabela tiver linhas, a menos que a sessao confirme (SET zippy.rollback_confirmado = 'sim') depois de exportar os dados.
DO $$
DECLARE
    t TEXT;
    possui BOOLEAN;
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    FOREACH t IN ARRAY ARRAY['crm_assistente_uso', 'crm_assistente_auditoria', 'crm_assistente_entrada', 'crm_assistente_consentimento'] LOOP
        IF to_regclass(t) IS NOT NULL THEN
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', t) INTO possui;
            IF possui THEN
                RAISE EXCEPTION 'Rollback recusado: % tem dados (consentimento, custo e auditoria do assistente). Exporte e rode com SET zippy.rollback_confirmado = ''sim''.', t;
            END IF;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS crm_assistente_uso;
DROP TABLE IF EXISTS crm_assistente_auditoria;
DROP TABLE IF EXISTS crm_assistente_entrada;
DROP TABLE IF EXISTS crm_assistente_consentimento;

COMMIT;
