BEGIN;

-- Nao apaga historico: so e seguro antes do primeiro estorno informado.
DO $$
BEGIN
    IF to_regclass('pagamento_estorno_informado') IS NOT NULL AND EXISTS (SELECT 1 FROM pagamento_estorno_informado) THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem estornos informados';
    END IF;
END $$;

DROP TABLE IF EXISTS pagamento_estorno_informado;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261201_01_pagamento_estorno_informado';

COMMIT;
