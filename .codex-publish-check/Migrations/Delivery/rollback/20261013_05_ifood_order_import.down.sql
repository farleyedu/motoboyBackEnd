BEGIN;
DO $$ BEGIN
    IF to_regclass('ifood_order_event_history') IS NOT NULL THEN
        RAISE EXCEPTION 'Rollback IF-07 requer remover IF-08 primeiro';
    END IF;
END $$;
LOCK TABLE ifood_order_details_cache,ifood_order_items,ifood_order_links,pedido IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_details_cache) OR EXISTS(SELECT 1 FROM ifood_order_items)
       OR EXISTS(SELECT 1 FROM ifood_order_links) THEN
        RAISE EXCEPTION 'Rollback IF-07 recusado: pedidos/cache em uso';
    END IF;
END $$;
DROP TRIGGER IF EXISTS tr_ifood_import_operational_guard ON pedido;
DROP FUNCTION IF EXISTS ifood_protect_imported_order();
DROP TABLE ifood_order_items;
DROP TABLE ifood_order_details_cache;
ALTER TABLE ifood_order_links DROP COLUMN IF EXISTS zippy_delivery_enabled;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261013_05_ifood_order_import';
COMMIT;
