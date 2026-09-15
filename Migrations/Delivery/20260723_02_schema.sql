BEGIN;

-- Cabecalho da rota ativa de cada motoboy: guarda so a versao monotonica usada
-- para concorrencia otimista em reordenacao (2.2 do plano). Uma linha por
-- motoboy que ja teve alguma atribuicao; nao precisa existir antes da primeira.
CREATE TABLE IF NOT EXISTS delivery_motoboy_route (
    motoboy_id INTEGER PRIMARY KEY REFERENCES motoboy(id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    version BIGINT NOT NULL DEFAULT 0,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Cada parada da fila de um motoboy: um pedido, uma posicao, um estado.
-- stop_status: 'assigned' (na fila, aguardando vez) | 'en_route' (entrega atual)
--            | 'completed' | 'canceled' | 'removed' (devolvido para Pendente).
CREATE TABLE IF NOT EXISTS delivery_route_stops (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    pedido_id INTEGER NOT NULL REFERENCES pedido(id),
    position INTEGER NOT NULL,
    stop_status TEXT NOT NULL,
    assigned_by_user_id INTEGER NULL,
    assigned_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at_utc TIMESTAMPTZ NULL,
    completed_at_utc TIMESTAMPTZ NULL,
    canceled_at_utc TIMESTAMPTZ NULL,
    removed_at_utc TIMESTAMPTZ NULL,
    cancel_reason TEXT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_motoboy_active
    ON delivery_route_stops (motoboy_id, position)
    WHERE stop_status IN ('assigned', 'en_route');

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_estabelecimento
    ON delivery_route_stops (estabelecimento_id, stop_status);

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_pedido
    ON delivery_route_stops (pedido_id);

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_history_by_motoboy
    ON delivery_route_stops (motoboy_id, updated_at_utc DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260723_02_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
