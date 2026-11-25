BEGIN;

-- Remove apenas os indices de leitura; nenhum dado de pedido e tocado.
DROP INDEX IF EXISTS ix_pedido_estabelecimento_origem_id;
DROP INDEX IF EXISTS ix_pedido_estabelecimento_id;

DELETE FROM delivery_tracking_schema_versions WHERE version='20261018_02_pedido_lista_indices';
COMMIT;
