BEGIN;

DROP INDEX IF EXISTS ix_cardapio_pedido_aviso_pagamento;
ALTER TABLE cardapio_pedido_publico DROP COLUMN IF EXISTS aviso_pagamento_pendente;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261214_01_cardapio_aviso_pagamento';

COMMIT;
