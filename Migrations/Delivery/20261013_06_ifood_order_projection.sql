BEGIN;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261013_05_ifood_order_import') THEN
        RAISE EXCEPTION 'IF-08 requer IF-07';
    END IF;
END $$;
ALTER TABLE ifood_order_snapshots
    ADD COLUMN IF NOT EXISTS status_occurred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS preparation_occurred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS data_occurred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS cancellation_occurred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS tracking_occurred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS cancellation_state TEXT NOT NULL DEFAULT 'none' CHECK(cancellation_state IN('none','requested','rejected','cancelled')),
    ADD COLUMN IF NOT EXISTS delivery_state TEXT NULL,
    ADD COLUMN IF NOT EXISTS data_is_current BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE ifood_disputes ADD COLUMN IF NOT EXISTS opened_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS settled_at_utc TIMESTAMPTZ NULL;
CREATE TABLE IF NOT EXISTS ifood_order_event_history (
    inbox_id BIGINT PRIMARY KEY REFERENCES ifood_events_inbox(id),
    merchant_id UUID NOT NULL,estabelecimento_id UUID NOT NULL,external_order_id UUID NULL,event_id UUID NOT NULL,
    event_code TEXT NOT NULL,occurred_at_utc TIMESTAMPTZ NULL,
    outcome TEXT NOT NULL CHECK(outcome IN('applied','ignored','review')),reason_code TEXT NULL,
    projection_transaction_id XID8 NOT NULL DEFAULT pg_current_xact_id(),
    projected_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(merchant_id,event_id),
    FOREIGN KEY(merchant_id,estabelecimento_id) REFERENCES ifood_connections(merchant_id,estabelecimento_id)
);
CREATE INDEX IF NOT EXISTS ix_ifood_projection_order_history ON ifood_order_event_history(merchant_id,external_order_id,occurred_at_utc);
-- Settlement antes da abertura: não inventa prazo/action para satisfazer a disputa original.
CREATE TABLE IF NOT EXISTS ifood_dispute_settlements (
    merchant_id UUID NOT NULL,dispute_id UUID NOT NULL CHECK(dispute_id<>'00000000-0000-0000-0000-000000000000'),
    external_order_id UUID NOT NULL,estabelecimento_id UUID NOT NULL,
    occurred_at_utc TIMESTAMPTZ NOT NULL,status TEXT NOT NULL,payload JSONB NOT NULL CHECK(jsonb_typeof(payload)='object'),
    PRIMARY KEY(merchant_id,dispute_id),
    FOREIGN KEY(merchant_id,external_order_id,estabelecimento_id) REFERENCES ifood_order_links(merchant_id,external_order_id,estabelecimento_id)
);
CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id AND estabelecimento_id=OLD.id_estabelecimento AND NOT zippy_delivery_enabled)
       AND (NEW.status_pedido IS DISTINCT FROM OLD.status_pedido OR NEW.motoboy_responsavel IS DISTINCT FROM OLD.motoboy_responsavel) THEN
        -- Exceção somente para CAN projetado nesta transação, com reserva/autorização atuais.
        IF NEW.status_pedido=4 AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel
           AND EXISTS(SELECT 1 FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
              JOIN ifood_order_event_history h USING(merchant_id,external_order_id,estabelecimento_id)
              JOIN ifood_events_inbox e ON e.id=h.inbox_id
              JOIN ifood_connections c ON c.merchant_id=l.merchant_id AND c.estabelecimento_id=l.estabelecimento_id
              WHERE l.pedido_id=OLD.id AND l.estabelecimento_id=OLD.id_estabelecimento AND s.remote_status='CANCELLED'
              AND h.outcome='applied' AND h.event_code='CANCELLED' AND h.projection_transaction_id=pg_current_xact_id()
              AND e.state='processing' AND e.lease_until_utc>clock_timestamp()
              AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL) THEN
            RETURN NEW;
        END IF;
        RAISE EXCEPTION 'Pedido iFood requer operacao oficial' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
INSERT INTO delivery_tracking_schema_versions(version)VALUES('20261013_06_ifood_order_projection') ON CONFLICT DO NOTHING;
COMMIT;
