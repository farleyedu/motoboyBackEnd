BEGIN;

-- Atualização de detalhes oficiais também é auditada. Não altera status, flags ou lojas.
ALTER TABLE ifood_reconciliation_audit DROP CONSTRAINT IF EXISTS ifood_reconciliation_audit_action_check;
ALTER TABLE ifood_reconciliation_audit DROP CONSTRAINT IF EXISTS ck_ifood_reconciliation_action;
ALTER TABLE ifood_reconciliation_audit ADD CONSTRAINT ck_ifood_reconciliation_action
    CHECK (action IN ('requeue','dismiss','resolve_review','refresh_details'));

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261015_02_ifood_command_panel') ON CONFLICT DO NOTHING;
COMMIT;
