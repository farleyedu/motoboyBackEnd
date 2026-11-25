-- Conferencia da 20261102_60 (somente leitura). Cada linha deve sair com ok = true.
SELECT 'pagamento.mp_order_id' AS item, EXISTS (
    SELECT 1 FROM information_schema.columns WHERE table_name = 'pagamento' AND column_name = 'mp_order_id') AS ok
UNION ALL SELECT 'ux_pagamento_mp_order', to_regclass('ux_pagamento_mp_order') IS NOT NULL;
