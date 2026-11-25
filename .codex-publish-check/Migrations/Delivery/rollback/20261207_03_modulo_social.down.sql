-- O Postgres nao remove valor de enum. Rollback seguro: tirar SOCIAL das lojas e dos pacotes; o valor fica sem uso.
BEGIN;
UPDATE estabelecimentos SET modulos_ativos = array_remove(modulos_ativos, 'SOCIAL'::modulo_enum) WHERE 'SOCIAL'::modulo_enum = ANY (modulos_ativos);
UPDATE pacotes_comerciais SET modulos_ativos = array_remove(modulos_ativos, 'SOCIAL'::modulo_enum) WHERE 'SOCIAL'::modulo_enum = ANY (modulos_ativos);
DELETE FROM delivery_tracking_schema_versions WHERE version IN ('20261207_04_social_lojas', '20261207_03_modulo_social');
COMMIT;
