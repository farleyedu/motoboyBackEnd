BEGIN;
DROP TABLE IF EXISTS cliente_sessoes;
ALTER TABLE clientes DROP COLUMN IF EXISTS endereco_historico_importado;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261010_01_cliente_sessoes';
COMMIT;
