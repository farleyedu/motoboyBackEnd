BEGIN;

-- Desfaz 20261102_60_mercadopago_orders. Pagamentos criados como order perdem o vinculo com a order (nao da mais para estorna-los pela Gestao).
DROP INDEX IF EXISTS ux_pagamento_mp_order;
ALTER TABLE pagamento DROP COLUMN IF EXISTS mp_order_id;

COMMIT;
