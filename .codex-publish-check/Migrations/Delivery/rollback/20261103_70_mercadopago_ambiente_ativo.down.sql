BEGIN;

-- Desfaz 20261103_70_mercadopago_ambiente_ativo. O servidor volta a usar Payments:MercadoPago:Ambiente da configuracao.
DROP TABLE IF EXISTS mp_ambiente_ativo;

COMMIT;
