BEGIN;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM delivery_tracking_schema_versions WHERE version='20261013_04_ifood_event_ingress') THEN
        RAISE EXCEPTION 'IF-07 requer IF-06';
    END IF;
END $$;
CREATE TABLE IF NOT EXISTS ifood_order_details_cache (
    merchant_id UUID NOT NULL,
    external_order_id UUID NOT NULL CHECK(external_order_id<>'00000000-0000-0000-0000-000000000000'),
    estabelecimento_id UUID NOT NULL,
    payload JSONB NULL CHECK(payload IS NULL OR jsonb_typeof(payload)='object'),
    lease_id UUID NULL,lease_until_utc TIMESTAMPTZ NULL,
    next_fetch_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    fetched_at_utc TIMESTAMPTZ NULL,last_error_code TEXT NULL,
    PRIMARY KEY(merchant_id,external_order_id),
    FOREIGN KEY(merchant_id,estabelecimento_id) REFERENCES ifood_connections(merchant_id,estabelecimento_id),
    CHECK((lease_id IS NULL AND lease_until_utc IS NULL) OR (lease_id IS NOT NULL AND lease_until_utc IS NOT NULL))
);
CREATE TABLE IF NOT EXISTS ifood_order_items (
    merchant_id UUID NOT NULL,external_order_id UUID NOT NULL,estabelecimento_id UUID NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),external_item_id TEXT NULL,name TEXT NOT NULL,
    quantity NUMERIC NOT NULL CHECK(quantity>0),unit TEXT NULL,
    unit_price NUMERIC NULL CHECK(unit_price>=0),price NUMERIC(18,2) NULL CHECK(price>=0),
    options_price NUMERIC(18,2) NULL CHECK(options_price>=0),total_price NUMERIC(18,2) NULL CHECK(total_price>=0),
    observations TEXT NULL,options JSONB NOT NULL,
    PRIMARY KEY(merchant_id,external_order_id,ordinal),
    FOREIGN KEY(merchant_id,external_order_id,estabelecimento_id)
        REFERENCES ifood_order_links(merchant_id,external_order_id,estabelecimento_id)
);
-- Trava operacional temporária: IF-09/IF-12 definirão habilitação por capacidades remotas.
ALTER TABLE ifood_order_links ADD COLUMN IF NOT EXISTS zippy_delivery_enabled BOOLEAN NOT NULL DEFAULT FALSE;
CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id AND estabelecimento_id=OLD.id_estabelecimento
        AND NOT zippy_delivery_enabled)
        AND (NEW.status_pedido IS DISTINCT FROM OLD.status_pedido OR NEW.motoboy_responsavel IS DISTINCT FROM OLD.motoboy_responsavel) THEN
        RAISE EXCEPTION 'Pedido iFood requer operacao oficial' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS tr_ifood_import_operational_guard ON pedido;
CREATE TRIGGER tr_ifood_import_operational_guard BEFORE UPDATE OF status_pedido,motoboy_responsavel ON pedido
FOR EACH ROW EXECUTE FUNCTION ifood_protect_imported_order();
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261013_05_ifood_order_import') ON CONFLICT DO NOTHING;
COMMIT;
