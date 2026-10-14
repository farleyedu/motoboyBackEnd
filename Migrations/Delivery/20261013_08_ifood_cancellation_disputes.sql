BEGIN;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261013_07_ifood_outbound_commands') THEN
        RAISE EXCEPTION 'IF-10 requer IF-09';
    END IF;
END $$;
ALTER TABLE ifood_disputes ADD COLUMN IF NOT EXISTS revision BIGINT NOT NULL DEFAULT 1 CHECK(revision>0);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_dispute_order_identity ON ifood_disputes(merchant_id,dispute_id,external_order_id,estabelecimento_id);
ALTER TABLE ifood_outbound_commands ADD COLUMN IF NOT EXISTS target_dispute_id UUID NULL,
    ADD COLUMN IF NOT EXISTS cancellation_requested_event_id UUID NULL;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='ifood_outbound_commands'::regclass AND conname='fk_ifood_response_dispute_identity') THEN
        ALTER TABLE ifood_outbound_commands ADD CONSTRAINT fk_ifood_response_dispute_identity
        FOREIGN KEY(merchant_id,target_dispute_id,external_order_id,estabelecimento_id)
        REFERENCES ifood_disputes(merchant_id,dispute_id,external_order_id,estabelecimento_id);
    END IF;
END $$;
LOCK TABLE ifood_outbound_commands IN SHARE ROW EXCLUSIVE MODE;
DROP INDEX IF EXISTS ux_ifood_active_food_command;
CREATE UNIQUE INDEX ux_ifood_active_food_command ON ifood_outbound_commands(merchant_id,external_order_id)
WHERE command_type IN('confirm','start_preparation','ready_to_pickup','dispatch','request_cancellation') AND state IN('queued','processing','uncertain','review');
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_active_dispute_response ON ifood_outbound_commands(merchant_id,target_dispute_id)
WHERE target_dispute_id IS NOT NULL AND state IN('queued','processing','uncertain','review');
CREATE TABLE IF NOT EXISTS ifood_cancellation_reason_snapshots (
    catalog_id UUID PRIMARY KEY CHECK(catalog_id<>'00000000-0000-0000-0000-000000000000'),
    merchant_id UUID NOT NULL,external_order_id UUID NOT NULL,estabelecimento_id UUID NOT NULL,
    expected_order_revision BIGINT NOT NULL CHECK(expected_order_revision>0),
    reasons JSONB NOT NULL CHECK(jsonb_typeof(reasons)='array'),fetched_at_utc TIMESTAMPTZ NOT NULL,expires_at_utc TIMESTAMPTZ NOT NULL,
    CHECK(expires_at_utc>fetched_at_utc),
    FOREIGN KEY(merchant_id,external_order_id,estabelecimento_id) REFERENCES ifood_order_links(merchant_id,external_order_id,estabelecimento_id)
);
CREATE INDEX IF NOT EXISTS ix_ifood_reasons_order ON ifood_cancellation_reason_snapshots(merchant_id,external_order_id,expires_at_utc);
INSERT INTO delivery_tracking_schema_versions(version)VALUES('20261013_08_ifood_cancellation_disputes') ON CONFLICT DO NOTHING;
COMMIT;
