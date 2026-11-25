BEGIN;

-- Recusa se ja existe cobranca da Zippy pela InfinitePay: apagar a estrutura perderia o rastro de dinheiro recebido.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM infinitepay_checkout WHERE id_conta_zippy_versao IS NOT NULL)
       OR EXISTS (SELECT 1 FROM pagamento_intent WHERE id_infinitepay_zippy_versao IS NOT NULL)
       OR EXISTS (SELECT 1 FROM comercial_cobranca WHERE meio = 'link' OR pago_via = 'infinitepay') THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem cobrancas da Zippy pela InfinitePay.';
    END IF;
END $$;

ALTER TABLE comercial_cobranca DROP CONSTRAINT IF EXISTS ck_comercial_cobranca_meio;
ALTER TABLE comercial_cobranca DROP CONSTRAINT IF EXISTS ck_comercial_cobranca_pago_via;
ALTER TABLE comercial_cobranca ADD CONSTRAINT ck_comercial_cobranca_meio
    CHECK (meio IS NULL OR meio IN ('pix', 'cartao', 'cartao_recorrente', 'manual'));
ALTER TABLE comercial_cobranca ADD CONSTRAINT ck_comercial_cobranca_pago_via
    CHECK (pago_via IS NULL OR pago_via IN ('mercadopago', 'mp_recorrente', 'manual'));

ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (provedor = 'mercadopago' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'mercadopago' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NOT NULL)
);

ALTER TABLE infinitepay_checkout DROP CONSTRAINT IF EXISTS ck_infinitepay_checkout_conta;
ALTER TABLE infinitepay_checkout DROP COLUMN IF EXISTS id_conta_zippy_versao;
ALTER TABLE infinitepay_checkout ALTER COLUMN id_conta_versao SET NOT NULL;
ALTER TABLE pagamento_intent DROP COLUMN IF EXISTS id_infinitepay_zippy_versao;
DROP TABLE IF EXISTS infinitepay_conta_zippy_versao;
DROP TABLE IF EXISTS infinitepay_conta_zippy;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261202_01_infinitepay_conta_zippy';

COMMIT;
