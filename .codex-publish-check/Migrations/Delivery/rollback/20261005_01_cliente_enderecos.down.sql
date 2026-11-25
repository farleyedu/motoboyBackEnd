BEGIN;

DROP TABLE IF EXISTS cliente_enderecos;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261005_01_cliente_enderecos';

COMMIT;
