-- ATENCAO: destrutivo. Apaga o schema novo da fila/rota (Parte 2) e o
-- historico de paradas acumulado. Faca backup/export antes se precisar
-- preservar auditoria de atribuicoes/entregas.

BEGIN;

DROP TABLE IF EXISTS delivery_route_stops;
DROP TABLE IF EXISTS delivery_motoboy_route;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260723_02_schema';

COMMIT;
