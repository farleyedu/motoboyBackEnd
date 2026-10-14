BEGIN;
LOCK TABLE ifood_outbound_commands,ifood_disputes,ifood_cancellation_reason_snapshots IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_cancellation_reason_snapshots) OR EXISTS(SELECT 1 FROM ifood_outbound_commands
        WHERE command_type IN('request_cancellation','dispute_accept','dispute_reject','dispute_alternative'))
        OR EXISTS(SELECT 1 FROM ifood_disputes WHERE revision<>1) THEN
        RAISE EXCEPTION 'Rollback IF-10 recusado: motivos/operacoes/revisoes em uso';
    END IF;
END $$;
DROP TABLE ifood_cancellation_reason_snapshots;
DROP INDEX IF EXISTS ux_ifood_active_dispute_response;
DROP INDEX IF EXISTS ux_ifood_active_food_command;
CREATE UNIQUE INDEX ux_ifood_active_food_command ON ifood_outbound_commands(merchant_id,external_order_id)
WHERE command_type IN('confirm','start_preparation','ready_to_pickup','dispatch') AND state IN('queued','processing','uncertain','review');
ALTER TABLE ifood_outbound_commands DROP CONSTRAINT IF EXISTS fk_ifood_response_dispute_identity,
    DROP COLUMN IF EXISTS target_dispute_id,DROP COLUMN IF EXISTS cancellation_requested_event_id;
DROP INDEX IF EXISTS ux_ifood_dispute_order_identity;
ALTER TABLE ifood_disputes DROP COLUMN IF EXISTS revision;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261013_08_ifood_cancellation_disputes';
COMMIT;
