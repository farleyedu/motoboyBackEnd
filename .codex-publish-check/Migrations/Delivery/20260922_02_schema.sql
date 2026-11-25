BEGIN;

-- ---------------------------------------------------------------------------
-- Operacao do motoboy, transferencia entre motoboys e isolamento da fila por
-- estabelecimento. Plano: zippy-admin/docs/plano-implementacao-delivery-operacao-motoboy.md
-- Idempotente: pode rodar manualmente e depois no boot sem efeito duplicado.
-- ---------------------------------------------------------------------------

-- 1) Marcos e desfechos da parada, gravados pelas acoes do motoboy.
ALTER TABLE delivery_route_stops
    ADD COLUMN IF NOT EXISTS picked_up_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS arrived_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS failed_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS failure_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS refused_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS refusal_reason TEXT NULL,
    ADD COLUMN IF NOT EXISTS transferred_at_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS transfer_request_id BIGINT NULL,
    -- 'motoboy' ou 'operator': quem concluiu a entrega.
    ADD COLUMN IF NOT EXISTS completed_by TEXT NULL;

-- 2) Parametros operacionais por estabelecimento. Sem linha = padroes do codigo
--    (mesmo comportamento de hoje). A tela de parametros vira depois.
CREATE TABLE IF NOT EXISTS delivery_settings (
    estabelecimento_id UUID PRIMARY KEY REFERENCES estabelecimentos(id),
    -- 'direct': o proprio motoboy decide; 'establishment_approval': o painel aprova.
    transfer_policy TEXT NOT NULL DEFAULT 'direct',
    require_delivery_code BOOLEAN NOT NULL DEFAULT FALSE,
    allow_motoboy_reorder BOOLEAN NOT NULL DEFAULT TRUE,
    allow_motoboy_refuse BOOLEAN NOT NULL DEFAULT TRUE,
    updated_by_user_id INTEGER NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 3) Transferencia de pedido entre motoboys (fluxo e auditoria).
CREATE TABLE IF NOT EXISTS delivery_transfer_requests (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    pedido_id INTEGER NOT NULL REFERENCES pedido(id),
    from_motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    to_motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    -- pending_approval | completed | rejected | cancelled
    status TEXT NOT NULL,
    -- politica vigente no momento da solicitacao: direct | establishment_approval | operator
    policy TEXT NOT NULL,
    -- quem pediu: 'motoboy' ou 'operator'
    requested_by TEXT NOT NULL,
    requested_by_user_id INTEGER NULL,
    reason TEXT NULL,
    requested_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    decided_by_user_id INTEGER NULL,
    decided_at_utc TIMESTAMPTZ NULL,
    decision_note TEXT NULL,
    completed_at_utc TIMESTAMPTZ NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_delivery_transfer_requests_estabelecimento_status
    ON delivery_transfer_requests (estabelecimento_id, status, requested_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_delivery_transfer_requests_from_motoboy
    ON delivery_transfer_requests (from_motoboy_id, status);

-- 4) Fila isolada por estabelecimento: o cabecalho da rota (versao usada na
--    concorrencia otimista) passa a existir por (motoboy, estabelecimento).
--    Antes, um motoboy vinculado a dois estabelecimentos misturava as filas.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM pg_constraint
         WHERE conname = 'delivery_motoboy_route_pkey'
           AND conrelid = 'delivery_motoboy_route'::regclass
           AND array_length(conkey, 1) = 1
    ) THEN
        ALTER TABLE delivery_motoboy_route DROP CONSTRAINT delivery_motoboy_route_pkey;
        ALTER TABLE delivery_motoboy_route
            ADD CONSTRAINT delivery_motoboy_route_pkey PRIMARY KEY (motoboy_id, estabelecimento_id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_queue
    ON delivery_route_stops (motoboy_id, estabelecimento_id, position)
    WHERE stop_status IN ('assigned', 'en_route');

-- Metricas do dia (entregas concluidas por estabelecimento).
CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_completed
    ON delivery_route_stops (estabelecimento_id, completed_at_utc)
    WHERE stop_status = 'completed';

-- Trajeto do dia por motoboy.
CREATE INDEX IF NOT EXISTS ix_motoboy_location_samples_trajectory
    ON motoboy_location_samples (estabelecimento_id, motoboy_id, captured_at_utc);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260922_02_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
