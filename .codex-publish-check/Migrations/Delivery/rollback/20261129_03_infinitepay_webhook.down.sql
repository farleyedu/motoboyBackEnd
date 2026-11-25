BEGIN;

-- Nao apaga historico: so e seguro antes do primeiro aviso recebido.
DO $$
BEGIN
    IF to_regclass('infinitepay_webhook_evento') IS NOT NULL AND EXISTS (SELECT 1 FROM infinitepay_webhook_evento) THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem avisos InfinitePay';
    END IF;
    IF to_regclass('infinitepay_pendencia') IS NOT NULL AND EXISTS (SELECT 1 FROM infinitepay_pendencia) THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem pendencias InfinitePay';
    END IF;
    IF EXISTS (SELECT 1 FROM infinitepay_checkout WHERE confirmado_em IS NOT NULL) THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem pagamentos InfinitePay confirmados';
    END IF;
END $$;

DROP TABLE IF EXISTS infinitepay_pendencia;
DROP TABLE IF EXISTS infinitepay_webhook_evento;
DROP INDEX IF EXISTS ux_infinitepay_checkout_transaction;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS paid_amount_centavos;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS amount_confirmado_centavos;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS capture_method;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS invoice_slug;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS transaction_nsu;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS confirmado_em;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261129_03_infinitepay_webhook';

COMMIT;
