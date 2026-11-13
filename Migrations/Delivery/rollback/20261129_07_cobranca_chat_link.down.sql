BEGIN;

-- Nao apaga historico: so e seguro antes da primeira cobranca por link.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM cobranca_chat WHERE modalidade = 'link')
       OR EXISTS (SELECT 1 FROM pagamento_intent WHERE provedor = 'infinitepay' AND origem = 'chat') THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem cobrancas do chat por link de pagamento';
    END IF;
END $$;

ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (provedor = 'mercadopago' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'mercadopago' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem = 'cardapio' AND conta_tipo = 'loja'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NOT NULL)
);
ALTER TABLE cobranca_chat_config DROP COLUMN IF EXISTS link_pagamento_habilitado;
ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS ck_cobranca_chat_modalidade;
ALTER TABLE cobranca_chat DROP COLUMN IF EXISTS modalidade;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261129_07_cobranca_chat_link';

COMMIT;
