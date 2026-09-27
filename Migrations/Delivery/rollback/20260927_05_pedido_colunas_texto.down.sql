-- Desfaz 20260927_05_pedido_colunas_texto: so o registro no ledger. As colunas continuam TEXT (alargar
-- e seguro e nao se reverte, mesmo padrao usado para motoboy.avatar em 20260930_01).
BEGIN;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260927_05_pedido_colunas_texto';
COMMIT;
