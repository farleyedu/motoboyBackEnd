BEGIN;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_reconciliation_audit WHERE action='refresh_details') THEN
        RAISE EXCEPTION 'Histórico de atualização oficial existente; preservar auditoria e corrigir por migration aditiva.';
    END IF;
END $$;
ALTER TABLE ifood_reconciliation_audit DROP CONSTRAINT IF EXISTS ck_ifood_reconciliation_action;
ALTER TABLE ifood_reconciliation_audit ADD CONSTRAINT ifood_reconciliation_audit_action_check
    CHECK (action IN ('requeue','dismiss','resolve_review'));
DELETE FROM delivery_tracking_schema_versions WHERE version='20261015_02_ifood_command_panel';
COMMIT;
