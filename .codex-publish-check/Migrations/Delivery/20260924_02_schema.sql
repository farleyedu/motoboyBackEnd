BEGIN;

-- ---------------------------------------------------------------------------
-- Janela de pedidos do mapa (modo comando), configuravel por estabelecimento.
-- JSON com o modo e os parametros; sem valor = padrao do codigo (desde a meia-noite,
-- e pedido em aberto aparece sempre). Formato e validacao: OrderWindowRules.cs.
--   {"mode":"last_hours","hours":10,"alwaysShowOpenOrders":true}
--   {"mode":"shifts","shifts":[{"days":[1,2,3,4,5],"start":"18:00","end":"02:00"}],...}
--   {"mode":"custom","customFrom":"2026-09-24T18:00","customTo":"2026-09-25T02:00",...}
-- Idempotente: pode rodar manualmente e depois no boot sem efeito duplicado.
-- A API funciona sem esta coluna (le com tolerancia e cai no padrao), entao a ordem
-- entre deploy e migration nao derruba o painel.
-- ---------------------------------------------------------------------------

ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS order_window JSONB NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260924_02_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
