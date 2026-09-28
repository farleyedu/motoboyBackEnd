-- Desfaz 20261001_03_produto_atendimento.
BEGIN;
DROP TABLE IF EXISTS cardapio_produto_atendimento;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261001_03_produto_atendimento';
COMMIT;
