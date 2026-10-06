-- Desfaz o grupo de motoboys por estabelecimento. Destrutivo: apaga todo o historico do grupo.
BEGIN;
DROP TABLE IF EXISTS motoboy_group_message;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261006_03_motoboy_grupo';
COMMIT;
