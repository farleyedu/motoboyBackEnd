-- O Postgres nao remove valor de enum. Rollback seguro: tirar o modulo das lojas e dos pacotes; o valor 'MESAS' fica sem uso.
BEGIN;
UPDATE estabelecimentos SET modulos_ativos = array_remove(modulos_ativos, 'MESAS'::modulo_enum) WHERE 'MESAS'::modulo_enum = ANY (modulos_ativos);
UPDATE pacotes_comerciais SET modulos_ativos = array_remove(modulos_ativos, 'MESAS'::modulo_enum) WHERE 'MESAS'::modulo_enum = ANY (modulos_ativos);
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261203_01_modulo_mesas';
COMMIT;
