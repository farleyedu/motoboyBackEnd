BEGIN;
-- Recusa apagar obrigações/dados de ingresso. Somente infraestrutura vazia pode voltar.
LOCK TABLE ifood_events_inbox, ifood_event_rejections, ifood_polling_state IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_events_inbox) OR EXISTS(SELECT 1 FROM ifood_event_rejections)
       OR EXISTS(SELECT 1 FROM ifood_polling_state) THEN
        RAISE EXCEPTION 'Rollback IF-06 recusado: ingresso em uso';
    END IF;
END $$;
DROP INDEX IF EXISTS ix_ifood_inbox_ack_pending;
ALTER TABLE ifood_events_inbox DROP COLUMN IF EXISTS ack_attempts;
ALTER TABLE ifood_events_inbox DROP COLUMN IF EXISTS ack_application_key;
ALTER TABLE ifood_events_inbox DROP COLUMN IF EXISTS ack_next_attempt_at_utc;
ALTER TABLE ifood_events_inbox DROP COLUMN IF EXISTS ack_last_error_code;
DROP TABLE ifood_event_rejections;
DROP TABLE ifood_polling_state;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261013_04_ifood_event_ingress';
COMMIT;
