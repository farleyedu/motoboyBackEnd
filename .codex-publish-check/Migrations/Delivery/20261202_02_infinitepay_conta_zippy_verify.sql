-- Verificacao da 20261202_01_infinitepay_conta_zippy: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela conta zippy' AS item, to_regclass('infinitepay_conta_zippy') IS NOT NULL AS ok
UNION ALL SELECT 'tabela versao zippy', to_regclass('infinitepay_conta_zippy_versao') IS NOT NULL
UNION ALL SELECT 'coluna intent', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'pagamento_intent' AND column_name = 'id_infinitepay_zippy_versao')
UNION ALL SELECT 'coluna checkout', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'infinitepay_checkout' AND column_name = 'id_conta_zippy_versao')
UNION ALL SELECT 'checkout: uma conta so', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_infinitepay_checkout_conta')
UNION ALL SELECT 'intent: regra de conta', pg_get_constraintdef((SELECT oid FROM pg_constraint WHERE conname = 'ck_pagamento_intent_conta')) LIKE '%id_infinitepay_zippy_versao IS NOT NULL%'
UNION ALL SELECT 'cobranca: meio link', pg_get_constraintdef((SELECT oid FROM pg_constraint WHERE conname = 'ck_comercial_cobranca_meio')) LIKE '%link%'
UNION ALL SELECT 'cobranca: pago_via infinitepay', pg_get_constraintdef((SELECT oid FROM pg_constraint WHERE conname = 'ck_comercial_cobranca_pago_via')) LIKE '%infinitepay%';
