-- Verificação da 20261214_01_cardapio_aviso_pagamento: só leitura. Cada linha deve ter ok = true.
SELECT 'coluna do aviso de pagamento' AS item, EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = 'cardapio_pedido_publico'
          AND column_name = 'aviso_pagamento_pendente') AS ok
UNION ALL SELECT 'índice dos avisos pendentes', to_regclass('ix_cardapio_pedido_aviso_pagamento') IS NOT NULL
UNION ALL SELECT 'versão registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261214_01_cardapio_aviso_pagamento');
