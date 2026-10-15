BEGIN;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_reconciliation_audit) THEN
        RAISE EXCEPTION 'Ha registros de auditoria de conciliacao; arquive-os antes do rollback IF-16/IF-17';
    END IF;
    IF EXISTS(SELECT 1 FROM ifood_app_settings WHERE retention_enabled) THEN
        RAISE EXCEPTION 'Desligue a limpeza automatica pela tela antes do rollback IF-16/IF-17';
    END IF;
END $$;
DROP TABLE IF EXISTS ifood_reconciliation_audit;
ALTER TABLE ifood_app_settings DROP CONSTRAINT IF EXISTS ck_ifood_settings_retention_days;
ALTER TABLE ifood_app_settings DROP COLUMN IF EXISTS retention_days;
ALTER TABLE ifood_app_settings DROP COLUMN IF EXISTS retention_enabled;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261015_01_ifood_reconciliation_retention';
COMMIT;
