-- Desfaz a 20261212_01_turno_entrega. Publique antes o servidor anterior: sem as tabelas o servidor novo não limita créditos.
BEGIN;
DROP TABLE IF EXISTS delivery_turno_credito;
DROP TABLE IF EXISTS delivery_turno_estado;
DROP TABLE IF EXISTS delivery_turno_entrega;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261212_01_turno_entrega';
COMMIT;
