-- Desfaz 20261127_03_acompanhamento_incidentes.sql. Apaga falhas, incidentes, linha do tempo, avisos e envios: e historico de auditoria.
-- Por padrao so roda com as tabelas vazias; com dados exige confirmacao explicita (veja a trava). Nao toca em pagamento nem mensagens.
BEGIN;

-- Trava: recusa se alguma tabela tiver linhas, a menos que a sessao confirme (SET zippy.rollback_confirmado = 'sim') depois de exportar os dados.
DO $$
DECLARE
    t TEXT;
    possui BOOLEAN;
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    FOREACH t IN ARRAY ARRAY['acompanhamento_incidente_envio', 'acompanhamento_aviso_lido', 'acompanhamento_aviso',
                             'acompanhamento_incidente_evento', 'acompanhamento_falha', 'acompanhamento_incidente'] LOOP
        IF to_regclass(t) IS NOT NULL THEN
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', t) INTO possui;
            IF possui THEN
                RAISE EXCEPTION 'Rollback recusado: % tem dados (falhas e incidentes sao historico de auditoria). Exporte e rode com SET zippy.rollback_confirmado = ''sim''.', t;
            END IF;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS acompanhamento_incidente_envio;
DROP TABLE IF EXISTS acompanhamento_aviso_lido;
DROP TABLE IF EXISTS acompanhamento_aviso;
DROP TABLE IF EXISTS acompanhamento_incidente_evento;
DROP TABLE IF EXISTS acompanhamento_falha;
DROP TABLE IF EXISTS acompanhamento_incidente;

COMMIT;
