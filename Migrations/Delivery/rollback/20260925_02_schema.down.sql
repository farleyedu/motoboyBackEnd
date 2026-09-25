BEGIN;

-- Desfaz 20260925_02_schema.sql. DESTRUTIVO: apaga os itens estruturados e as colunas novas.
-- A coluna legada pedido.items nunca foi removida, entao o painel continua mostrando os itens.
-- Rode depois dos .down de 05, 04 e 03.
DROP TABLE IF EXISTS pedido_item;

ALTER TABLE pedido DROP COLUMN IF EXISTS desconto;
ALTER TABLE pedido DROP COLUMN IF EXISTS taxa_entrega;
ALTER TABLE pedido DROP COLUMN IF EXISTS subtotal;
ALTER TABLE pedido DROP COLUMN IF EXISTS cliente_id;
ALTER TABLE pedido DROP COLUMN IF EXISTS conversa_id;
ALTER TABLE pedido DROP COLUMN IF EXISTS origem_ref;
ALTER TABLE pedido DROP COLUMN IF EXISTS origem;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260925_02_schema';

-- O valor 'PEDIDOS' do enum modulo_enum (20260925_01) NAO e removido: o Postgres nao tem
-- DROP VALUE para enum. Ele e inofensivo; basta nao usa-lo.

COMMIT;
