BEGIN;

-- Idempotencia da ingestao iFood: uma mesma id_ifood nao pode gerar dois pedidos.
-- Se esta migration falhar por violacao de unicidade, rode o relatorio de
-- duplicidade em 20260723_01_preflight.sql e resolva manualmente antes de repetir.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pedido_id_ifood
    ON pedido (id_ifood)
    WHERE id_ifood IS NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_route_stop_status') THEN
        ALTER TABLE delivery_route_stops
            ADD CONSTRAINT ck_delivery_route_stop_status
            CHECK (stop_status IN ('assigned', 'en_route', 'completed', 'canceled', 'removed'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_delivery_route_stop_position') THEN
        ALTER TABLE delivery_route_stops
            ADD CONSTRAINT ck_delivery_route_stop_position
            CHECK (position > 0);
    END IF;
END $$;

-- No maximo uma parada por posicao ativa por motoboy.
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_route_stop_position_active
    ON delivery_route_stops (motoboy_id, position)
    WHERE stop_status IN ('assigned', 'en_route');

-- Um pedido nao pode estar em duas paradas ativas (mesmo motoboy ou motoboys diferentes).
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_route_stop_pedido_active
    ON delivery_route_stops (pedido_id)
    WHERE stop_status IN ('assigned', 'en_route');

-- No maximo um pedido em rota (entrega atual) por motoboy.
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_route_stop_en_route_per_motoboy
    ON delivery_route_stops (motoboy_id)
    WHERE stop_status = 'en_route';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260723_04_constraints')
ON CONFLICT (version) DO NOTHING;

COMMIT;
