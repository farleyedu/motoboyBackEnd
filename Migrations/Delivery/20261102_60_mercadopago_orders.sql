BEGIN;

-- ---------------------------------------------------------------------------
-- Mercado Pago: Pix e cartao do checkout passam da API v1/payments para a Orders API (v1/orders).
--   pagamento.mp_order_id  id textual da order (ORD...), usado para consultar, cancelar e estornar.
--                          mp_payment_id continua sendo o id numerico do pagamento (reference_id da Orders), entao nada mais muda.
--                          Fica NULL nos pagamentos da API v1 (cobrancas recorrentes das assinaturas).
-- Requer 20261031_01 (nucleo Mercado Pago). Aditiva e idempotente; rollback em rollback/20261102_60_mercadopago_orders.down.sql.
-- ---------------------------------------------------------------------------

ALTER TABLE pagamento ADD COLUMN IF NOT EXISTS mp_order_id TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_pagamento_mp_order ON pagamento (ambiente, mp_order_id) WHERE mp_order_id IS NOT NULL;

COMMIT;
