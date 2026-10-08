DO $$
BEGIN
    IF to_regclass('delivery_client_chat_reaction') IS NULL THEN
        RAISE EXCEPTION 'Falta a migration 20261008_06_comunicacao_correcoes';
    END IF;
    IF EXISTS(SELECT 1 FROM delivery_chat_push_outbox WHERE state='queued'
        AND (recipient_session_id IS NULL OR recipient_motoboy_id IS NULL OR recipient_estabelecimento_id IS NULL)) THEN
        RAISE EXCEPTION 'Push pendente sem identidade imutavel';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version='20261008_06_comunicacao_correcoes';
