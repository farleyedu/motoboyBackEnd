BEGIN;

-- Publicar no SignalR nao mantem transacao nem conexao aberta.
-- A reserva expira se o processo cair; eventos sem ACK podem ser reenviados.
CREATE TABLE IF NOT EXISTS delivery_outbox_publisher_lease (
    singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton = TRUE),
    lease_id UUID NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL
);

-- Consulta de eventos anteriores pendentes do mesmo alvo/motoboy, usada no retry.
CREATE INDEX IF NOT EXISTS ix_delivery_outbox_pending_aggregate
    ON delivery_realtime_outbox (target_group, motoboy_id, occurred_at_utc, event_id)
    WHERE published_at_utc IS NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261007_01_outbox_publisher_lease') ON CONFLICT (version) DO NOTHING;

COMMIT;
