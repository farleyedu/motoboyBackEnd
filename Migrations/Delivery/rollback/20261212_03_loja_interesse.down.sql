-- Desfaz a 20261212_03_loja_interesse. Com registros gravados, para: o histórico não se recupera (apague à mão se for isso mesmo).
BEGIN;
DO $$
BEGIN
    IF to_regclass('loja_interesse') IS NOT NULL AND EXISTS (SELECT 1 FROM loja_interesse) THEN
        RAISE EXCEPTION 'loja_interesse tem registros; o rollback não apaga histórico';
    END IF;
END $$;
DROP TABLE IF EXISTS loja_interesse;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261212_03_loja_interesse';
COMMIT;
