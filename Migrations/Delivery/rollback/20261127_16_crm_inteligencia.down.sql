-- Desfaz 20261127_16_crm_inteligencia.sql. Apaga objecoes, recomendacoes, feedback e auditoria de objecoes.
-- Por padrao so roda com as tabelas vazias; com dados exige confirmacao explicita (exporte antes). Nao toca em oportunidades,
-- historico do CRM, pagamentos nem no assistente de audio (reverta esta ANTES de 20261127_15).
BEGIN;

-- Trava: recusa se alguma tabela tiver linhas, a menos que a sessao confirme (SET zippy.rollback_confirmado = 'sim') depois de exportar os dados.
DO $$
DECLARE
    t TEXT;
    possui BOOLEAN;
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    FOREACH t IN ARRAY ARRAY['crm_recomendacao_evento', 'crm_recomendacao', 'crm_objecao_auditoria', 'crm_objecao'] LOOP
        IF to_regclass(t) IS NOT NULL THEN
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', t) INTO possui;
            IF possui THEN
                RAISE EXCEPTION 'Rollback recusado: % tem dados (objecoes e recomendacoes sao historico comercial). Exporte e rode com SET zippy.rollback_confirmado = ''sim''.', t;
            END IF;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS crm_recomendacao_evento;
DROP TABLE IF EXISTS crm_recomendacao;
DROP TABLE IF EXISTS crm_objecao_auditoria;
DROP TABLE IF EXISTS crm_objecao;

COMMIT;
