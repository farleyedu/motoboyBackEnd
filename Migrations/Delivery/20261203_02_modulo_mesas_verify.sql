-- Verificacao da 20261203_01_modulo_mesas: so leitura. Cada linha deve ter ok = true.
SELECT 'enum MESAS' AS item, EXISTS (
    SELECT 1 FROM pg_enum e JOIN pg_type t ON t.oid = e.enumtypid WHERE t.typname = 'modulo_enum' AND e.enumlabel = 'MESAS') AS ok
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261203_01_modulo_mesas');
