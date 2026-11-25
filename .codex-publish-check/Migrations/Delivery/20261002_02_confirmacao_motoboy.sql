BEGIN;

-- ---------------------------------------------------------------------------
-- Confirmacao do motoboy: a rota enviada pelo atendente fica "oferecida" ate o motoboy aceitar
-- ou recusar (status_pedido = 8, AguardandoMotoboy). Tudo aditivo e idempotente. Sem esta migration
-- a API segue como antes: a atribuicao continua virando entrega na hora.
-- ---------------------------------------------------------------------------

-- 1) A parada continua 'assigned' (ocupa posicao e lugar na fila); o que a marca como oferta e
--    offered_at_utc preenchido. offer_id agrupa as paradas enviadas juntas (a rota).
ALTER TABLE delivery_route_stops
    ADD COLUMN IF NOT EXISTS offer_id UUID NULL,
    ADD COLUMN IF NOT EXISTS offered_at_utc TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_delivery_route_stops_offered
    ON delivery_route_stops (motoboy_id, estabelecimento_id, offered_at_utc)
    WHERE offered_at_utc IS NOT NULL AND stop_status = 'assigned';

-- 2) Parametros por estabelecimento. Padrao = comportamento de hoje (sem confirmacao).
ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS require_motoboy_acceptance BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS offer_timeout_minutes INTEGER NOT NULL DEFAULT 2;

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_offer_timeout;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_offer_timeout CHECK (offer_timeout_minutes BETWEEN 1 AND 60);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_02_confirmacao_motoboy')
ON CONFLICT (version) DO NOTHING;

COMMIT;
