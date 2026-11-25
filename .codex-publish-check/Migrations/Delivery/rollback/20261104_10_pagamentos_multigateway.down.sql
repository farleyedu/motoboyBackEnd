BEGIN;

-- Nao apaga historico InfinitePay. O rollback so e seguro antes do primeiro uso.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pagamento_intent WHERE provedor = 'infinitepay')
       OR EXISTS (SELECT 1 FROM infinitepay_conta_loja)
       OR EXISTS (SELECT 1 FROM pagamento_preferencia_provedor WHERE provedor = 'infinitepay') THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem configuracoes ou intencoes InfinitePay';
    END IF;
END $$;

ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (origem IN ('cardapio', 'chat') AND conta_tipo = 'loja' AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL)
    OR (origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy' AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL)
);

ALTER TABLE pagamento DROP CONSTRAINT IF EXISTS ck_pagamento_provedor;
ALTER TABLE pagamento_tentativa DROP CONSTRAINT IF EXISTS ck_pagamento_tentativa_provedor;
ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_provedor;

ALTER TABLE pagamento DROP COLUMN IF EXISTS referencia_provedor;
ALTER TABLE pagamento DROP COLUMN IF EXISTS provedor;
ALTER TABLE pagamento_tentativa DROP COLUMN IF EXISTS referencia_provedor;
ALTER TABLE pagamento_tentativa DROP COLUMN IF EXISTS provedor;
ALTER TABLE pagamento_intent DROP COLUMN IF EXISTS id_infinitepay_conta_versao;
ALTER TABLE pagamento_intent DROP COLUMN IF EXISTS provedor;

DROP TABLE IF EXISTS infinitepay_conta_versao;
DROP TABLE IF EXISTS infinitepay_conta_loja;
DROP TABLE IF EXISTS pagamento_preferencia_provedor;
DROP TABLE IF EXISTS pagamento_provedor_loja;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261104_10_pagamentos_multigateway';

COMMIT;
