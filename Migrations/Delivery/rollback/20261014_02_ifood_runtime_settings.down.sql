BEGIN;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_app_settings WHERE id=1 AND enabled) THEN
        RAISE EXCEPTION 'Desligue Ifood:Enabled pela tela de configuracao antes do rollback IF-13';
    END IF;
END $$;
DROP TABLE IF EXISTS ifood_app_settings;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261014_02_ifood_runtime_settings';
COMMIT;
