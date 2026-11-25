-- Desfaz 20261001_01_cardapio_schema. Ordem inversa das FKs.
BEGIN;
DROP TABLE IF EXISTS cardapio_pedido_publico;
DROP TABLE IF EXISTS cardapio_web_config;
DROP TABLE IF EXISTS cardapio_produto_grupo;
DROP TABLE IF EXISTS cardapio_grupo_adicional_item;
DROP TABLE IF EXISTS cardapio_grupo_adicional;
DROP TABLE IF EXISTS cardapio_produto;
DROP TABLE IF EXISTS cardapio_categoria;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261001_01_cardapio_schema';
COMMIT;
