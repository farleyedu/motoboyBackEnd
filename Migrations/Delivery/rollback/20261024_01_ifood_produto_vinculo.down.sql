BEGIN;

-- Apaga so os vinculos (dado de exibicao). Pedidos, snapshots e cardapio nao sao tocados: item sem vinculo continua valido.
DROP TABLE IF EXISTS ifood_produto_vinculo;
DROP INDEX IF EXISTS ux_cardapio_produto_id_estab;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261024_01_ifood_produto_vinculo';
COMMIT;
