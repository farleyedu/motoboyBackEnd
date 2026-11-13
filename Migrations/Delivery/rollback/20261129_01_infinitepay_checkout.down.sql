BEGIN;

-- Nao apaga historico: so e seguro antes do primeiro link InfinitePay.
DO $$
BEGIN
    IF to_regclass('infinitepay_checkout') IS NOT NULL AND EXISTS (SELECT 1 FROM infinitepay_checkout) THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem checkouts InfinitePay';
    END IF;
END $$;

DROP TABLE IF EXISTS infinitepay_checkout;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261129_01_infinitepay_checkout';

COMMIT;
