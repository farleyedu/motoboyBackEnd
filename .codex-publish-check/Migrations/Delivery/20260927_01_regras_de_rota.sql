BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 4 (regras de rota): pedido travado (ancora), retorno a loja e seus parametros.
-- Tudo aditivo e idempotente. Sem esta migration a API segue funcionando como antes: as
-- consultas tratam a ausencia das colunas e os comandos novos respondem 503 MIGRATION_PENDING.
-- ---------------------------------------------------------------------------

-- 1) Lock (D5, posicao absoluta): a parada travada e uma ancora na fila; so o estabelecimento destrava.
ALTER TABLE delivery_route_stops
    ADD COLUMN IF NOT EXISTS locked BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS locked_by_user_id INTEGER NULL,
    ADD COLUMN IF NOT EXISTS locked_at_utc TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_locked
    ON delivery_route_stops (motoboy_id, estabelecimento_id, position)
    WHERE locked AND stop_status IN ('assigned', 'en_route');

-- 2) Estado da rota do motoboy: 'idle' (sem retorno pendente) ou 'returning' (voltando a loja).
ALTER TABLE delivery_motoboy_route
    ADD COLUMN IF NOT EXISTS route_state TEXT NOT NULL DEFAULT 'idle',
    ADD COLUMN IF NOT EXISTS returning_since_utc TIMESTAMPTZ NULL;

ALTER TABLE delivery_motoboy_route DROP CONSTRAINT IF EXISTS ck_delivery_motoboy_route_state;
ALTER TABLE delivery_motoboy_route
    ADD CONSTRAINT ck_delivery_motoboy_route_state CHECK (route_state IN ('idle', 'returning'));

-- 3) Parametros por estabelecimento. Padrao = comportamento de hoje (motoboy livre ao concluir).
ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS require_return_to_store BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS store_return_radius_m INTEGER NOT NULL DEFAULT 80,
    ADD COLUMN IF NOT EXISTS auto_finish_route_on_return BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_store_return_radius;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_store_return_radius CHECK (store_return_radius_m BETWEEN 10 AND 2000);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260927_01_regras_de_rota')
ON CONFLICT (version) DO NOTHING;

COMMIT;
