BEGIN;

-- ---------------------------------------------------------------------------
-- Presenca de 7 horas + incidentes de desconexao.
--  * last_human_activity_at_utc: ultima acao humana do motoboy no turno (servidor). Heartbeat, GPS,
--    abertura de conexao e push NAO contam. Em turnos mobile, expires_at_utc passa a ser essa data + 7 h
--    (o fim do turno por inatividade); os filtros SQL existentes "expires_at_utc > NOW()" continuam validos.
--  * delivery_motoboy_disconnect_incidents: um incidente por turno encerrado com pedidos afetados.
--  * delivery_motoboy_disconnect_incident_items: pedidos afetados (etapa, pagamento e responsavel anterior).
--  * delivery_motoboy_disconnect_incident_actions: historico + idempotencia das acoes da loja.
-- Aditiva e idempotente; rollback em rollback/20261104_01_presenca_7h_incidentes.down.sql.
-- ---------------------------------------------------------------------------

ALTER TABLE motoboy_active_sessions ADD COLUMN IF NOT EXISTS last_human_activity_at_utc TIMESTAMPTZ NULL;

-- Turnos abertos recebem 7 horas completas a partir da publicacao (ninguem cai por causa da migracao);
-- turnos encerrados so guardam uma data coerente. Reaplicar nao estende nada: so mexe em quem ainda esta NULL.
UPDATE motoboy_active_sessions
   SET expires_at_utc = CASE WHEN origin = 'mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL
                             THEN NOW() + INTERVAL '7 hours' ELSE expires_at_utc END,
       last_human_activity_at_utc = CASE WHEN origin = 'mobile' AND ended_at_utc IS NULL AND revoked_at IS NULL
                                         THEN NOW()
                                         ELSE COALESCE(ended_at_utc, last_seen_at, last_heartbeat_at_utc, started_at_utc) END
 WHERE last_human_activity_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS delivery_motoboy_disconnect_incidents (
    incident_id                  UUID PRIMARY KEY,
    estabelecimento_id           UUID NOT NULL,
    motoboy_id                   INTEGER NOT NULL,
    session_id                   UUID NOT NULL,
    end_reason                   TEXT NOT NULL,
    ended_by_user_id             INTEGER NULL,
    status                       TEXT NOT NULL DEFAULT 'aberto' CHECK (status IN ('aberto', 'aguardando_retorno', 'resolvido')),
    version                      INTEGER NOT NULL DEFAULT 1,
    client_platform              TEXT NULL,
    opened_at_utc                TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    turn_ended_at_utc            TIMESTAMPTZ NOT NULL,
    last_contact_at_utc          TIMESTAMPTZ NULL,
    last_human_activity_at_utc   TIMESTAMPTZ NULL,
    last_latitude                DOUBLE PRECISION NULL,
    last_longitude               DOUBLE PRECISION NULL,
    last_accuracy_meters         DOUBLE PRECISION NULL,
    last_location_captured_at_utc TIMESTAMPTZ NULL,
    resolved_at_utc              TIMESTAMPTZ NULL,
    resolved_by_user_id          INTEGER NULL,
    updated_at_utc               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_disconnect_incident_session UNIQUE (session_id)
);

CREATE INDEX IF NOT EXISTS ix_disconnect_incidents_store_status
    ON delivery_motoboy_disconnect_incidents (estabelecimento_id, status, opened_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_disconnect_incidents_motoboy
    ON delivery_motoboy_disconnect_incidents (motoboy_id, estabelecimento_id);

CREATE TABLE IF NOT EXISTS delivery_motoboy_disconnect_incident_items (
    id                        BIGSERIAL PRIMARY KEY,
    incident_id               UUID NOT NULL REFERENCES delivery_motoboy_disconnect_incidents (incident_id),
    estabelecimento_id        UUID NOT NULL,
    pedido_id                 INTEGER NOT NULL,
    stop_id                   BIGINT NULL,
    previous_motoboy_id       INTEGER NOT NULL,
    collected                 BOOLEAN NOT NULL,
    stop_status_at_close      TEXT NULL,
    pedido_status_at_close    INTEGER NULL,
    picked_up_at_utc          TIMESTAMPTZ NULL,
    payment_snapshot          JSONB NOT NULL DEFAULT '{}'::jsonb,
    state                     TEXT NOT NULL DEFAULT 'pendente' CHECK (state IN ('pendente', 'devolvido', 'resolvido')),
    resolution                TEXT NULL,
    return_confirmed_at_utc   TIMESTAMPTZ NULL,
    return_confirmed_by_user_id INTEGER NULL,
    resolved_at_utc           TIMESTAMPTZ NULL,
    resolved_by_user_id       INTEGER NULL,
    created_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_disconnect_incident_item UNIQUE (incident_id, pedido_id)
);

CREATE INDEX IF NOT EXISTS ix_disconnect_items_pedido ON delivery_motoboy_disconnect_incident_items (pedido_id);
CREATE INDEX IF NOT EXISTS ix_disconnect_items_open
    ON delivery_motoboy_disconnect_incident_items (estabelecimento_id, incident_id) WHERE state <> 'resolvido';

CREATE TABLE IF NOT EXISTS delivery_motoboy_disconnect_incident_actions (
    id                    BIGSERIAL PRIMARY KEY,
    incident_id           UUID NOT NULL REFERENCES delivery_motoboy_disconnect_incidents (incident_id),
    estabelecimento_id    UUID NOT NULL,
    idempotency_key       TEXT NOT NULL,
    action_type           TEXT NOT NULL,
    pedido_ids            INTEGER[] NOT NULL DEFAULT '{}',
    destination_motoboy_id INTEGER NULL,
    reason                TEXT NULL,
    expected_version      INTEGER NULL,
    actor_user_id         INTEGER NOT NULL,
    state                 TEXT NOT NULL DEFAULT 'processando' CHECK (state IN ('processando', 'concluida')),
    result                JSONB NULL,
    created_at_utc        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at_utc      TIMESTAMPTZ NULL,
    CONSTRAINT uq_disconnect_action_key UNIQUE (incident_id, idempotency_key)
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261104_01_presenca_7h_incidentes') ON CONFLICT DO NOTHING;

COMMIT;
