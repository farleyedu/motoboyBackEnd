BEGIN;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_app_settings WHERE completion_enabled) THEN
        RAISE EXCEPTION 'Desligue a conclusao por codigo oficial pela tela antes do rollback IF-13 lote 2';
    END IF;
    IF EXISTS(SELECT 1 FROM ifood_connections WHERE zippy_delivery_enabled) THEN
        RAISE EXCEPTION 'Desligue a entrega Zippy de todas as lojas pela tela antes do rollback IF-13 lote 2';
    END IF;
END $$;
ALTER TABLE ifood_connections DROP COLUMN IF EXISTS zippy_delivery_enabled;
ALTER TABLE ifood_app_settings DROP COLUMN IF EXISTS completion_enabled;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261014_04_ifood_runtime_flags';
COMMIT;
