-- Sequencia das mensagens do pedido pago online: depois de avisar o cliente que falta o pagamento, o pre-pedido fica
-- marcado ate sair a mensagem "pagamento aprovado". Pedidos antigos ficam FALSE (nada e enviado em atraso).
-- Aditiva e idempotente. Rollback em rollback/20261214_01_cardapio_aviso_pagamento.down.sql.
BEGIN;

ALTER TABLE cardapio_pedido_publico
    ADD COLUMN IF NOT EXISTS aviso_pagamento_pendente boolean NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_aviso_pagamento
    ON cardapio_pedido_publico (pago_em) WHERE aviso_pagamento_pendente;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261214_01_cardapio_aviso_pagamento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
