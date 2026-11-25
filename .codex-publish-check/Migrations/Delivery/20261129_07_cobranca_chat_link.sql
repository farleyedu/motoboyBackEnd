BEGIN;

-- Cobranca do chat por Link de pagamento (Pix ou cartao) da InfinitePay. Aditivo: nada existente muda (modalidade padrao = pix).
ALTER TABLE cobranca_chat ADD COLUMN IF NOT EXISTS modalidade TEXT NOT NULL DEFAULT 'pix';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cobranca_chat_modalidade') THEN
        ALTER TABLE cobranca_chat ADD CONSTRAINT ck_cobranca_chat_modalidade CHECK (modalidade IN ('pix', 'link'));
    END IF;
END $$;

-- O proprietario liga o link por loja. Desligado por padrao.
ALTER TABLE cobranca_chat_config ADD COLUMN IF NOT EXISTS link_pagamento_habilitado BOOLEAN NOT NULL DEFAULT FALSE;

-- A intencao da InfinitePay tambem pode nascer do chat (antes so do cardapio). Mercado Pago e Zippy ficam exatamente como estavam.
ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (provedor = 'mercadopago' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'mercadopago' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NOT NULL)
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261129_07_cobranca_chat_link')
ON CONFLICT (version) DO NOTHING;

COMMIT;
