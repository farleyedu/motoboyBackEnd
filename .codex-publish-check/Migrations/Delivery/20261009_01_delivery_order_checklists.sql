BEGIN;
CREATE TABLE IF NOT EXISTS delivery_order_item_checks (
    stop_id bigint NOT NULL,
    estabelecimento_id uuid NOT NULL,
    motoboy_id int NOT NULL,
    stage text NOT NULL CHECK (stage IN ('pickup','delivery')),
    manifest jsonb NOT NULL,
    confirmation jsonb NOT NULL,
    confirmed_at_utc timestamptz NOT NULL DEFAULT NOW(),
    PRIMARY KEY (stop_id,stage)
);
CREATE INDEX IF NOT EXISTS ix_delivery_order_item_checks_owner
    ON delivery_order_item_checks(estabelecimento_id,motoboy_id,confirmed_at_utc DESC);
COMMIT;
