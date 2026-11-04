-- Rollback manual de 20261128_01_pedido_modalidade. Perde a modalidade gravada: o Caixa volta a nao saber separar retirada de delivery.
BEGIN;
ALTER TABLE pedido DROP CONSTRAINT IF EXISTS ck_pedido_modalidade;
ALTER TABLE pedido DROP COLUMN IF EXISTS modalidade;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261128_01_pedido_modalidade';
COMMIT;
