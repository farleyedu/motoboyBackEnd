BEGIN;
-- Executar somente com publicador desligado e a versao anterior preparada.
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM delivery_client_chat_reaction) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ha reacoes reais que devem ser preservadas';
    END IF;
END $$;
UPDATE delivery_chat_push_outbox SET state='dismissed' WHERE state IN ('queued','sending');
ALTER TABLE delivery_chat_push_outbox DROP COLUMN IF EXISTS recipient_estabelecimento_id;
ALTER TABLE delivery_chat_push_outbox DROP COLUMN IF EXISTS recipient_motoboy_id;
ALTER TABLE delivery_chat_push_outbox DROP COLUMN IF EXISTS recipient_session_id;
DROP TABLE delivery_client_chat_reaction;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261008_06_comunicacao_correcoes';
COMMIT;
