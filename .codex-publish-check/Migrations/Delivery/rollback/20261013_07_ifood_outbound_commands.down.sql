BEGIN;
DO $$ BEGIN
    IF to_regclass('ifood_cancellation_reason_snapshots') IS NOT NULL THEN
        RAISE EXCEPTION 'Rollback IF-09 requer remover IF-10 primeiro';
    END IF;
END $$;
LOCK TABLE ifood_command_attempts,ifood_outbound_commands IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_command_attempts) OR EXISTS(SELECT 1 FROM ifood_outbound_commands WHERE command_type IN('confirm','start_preparation','ready_to_pickup','dispatch')) THEN
        RAISE EXCEPTION 'Rollback IF-09 recusado: operacoes em uso';
    END IF;
END $$;
DROP TABLE ifood_command_attempts;
DROP INDEX IF EXISTS ux_ifood_active_food_command;
ALTER TABLE ifood_outbound_commands DROP COLUMN IF EXISTS send_started_at_utc,DROP COLUMN IF EXISTS http_accepted_at_utc,
    DROP COLUMN IF EXISTS confirmed_event_id,DROP COLUMN IF EXISTS not_before_utc,DROP COLUMN IF EXISTS deadline_at_utc;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261013_07_ifood_outbound_commands';
COMMIT;
