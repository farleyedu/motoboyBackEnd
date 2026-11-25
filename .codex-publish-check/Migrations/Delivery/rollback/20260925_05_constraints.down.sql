BEGIN;

-- Desfaz 20260925_05_constraints.sql. Nao apaga dados.
DROP INDEX IF EXISTS ix_pedido_item_pedido;
DROP INDEX IF EXISTS ux_pedido_origem_ref;
ALTER TABLE pedido DROP CONSTRAINT IF EXISTS ck_pedido_origem;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260925_05_constraints';

COMMIT;
