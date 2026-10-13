BEGIN;

-- IF-04: armazenamento passivo; nenhum worker, loja ou transporte é ativado aqui.
-- Vínculo composto impede associar o pedido de uma loja ao merchant de outra.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pedido_ifood_store_identity ON pedido (id, id_estabelecimento);

CREATE TABLE IF NOT EXISTS ifood_connections (
    merchant_id UUID PRIMARY KEY CHECK (merchant_id <> '00000000-0000-0000-0000-000000000000'),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','authorized','revoked','disabled')),
    authorized_at_utc TIMESTAMPTZ NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    last_event_at_utc TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (merchant_id, estabelecimento_id)
);

CREATE TABLE IF NOT EXISTS ifood_order_links (
    merchant_id UUID NOT NULL,
    external_order_id UUID NOT NULL CHECK (external_order_id <> '00000000-0000-0000-0000-000000000000'),
    estabelecimento_id UUID NOT NULL,
    pedido_id INTEGER NOT NULL,
    display_number TEXT NULL CHECK (display_number IS NULL OR btrim(display_number) <> ''),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (merchant_id, external_order_id),
    UNIQUE (pedido_id),
    UNIQUE (merchant_id, external_order_id, estabelecimento_id),
    FOREIGN KEY (merchant_id, estabelecimento_id) REFERENCES ifood_connections(merchant_id, estabelecimento_id),
    FOREIGN KEY (pedido_id, estabelecimento_id) REFERENCES pedido(id, id_estabelecimento)
);

CREATE TABLE IF NOT EXISTS ifood_events_inbox (
    id BIGSERIAL PRIMARY KEY,
    merchant_id UUID NOT NULL,
    estabelecimento_id UUID NOT NULL,
    event_id UUID NOT NULL CHECK (event_id <> '00000000-0000-0000-0000-000000000000'),
    external_order_id UUID NULL,
    event_code TEXT NOT NULL CHECK (btrim(event_code) <> ''),
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    state TEXT NOT NULL DEFAULT 'queued' CHECK (state IN ('queued','processing','processed','ignored','review','failed')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    lease_id UUID NULL,
    lease_until_utc TIMESTAMPTZ NULL,
    occurred_at_utc TIMESTAMPTZ NULL,
    received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    acknowledged_at_utc TIMESTAMPTZ NULL,
    processed_at_utc TIMESTAMPTZ NULL,
    next_attempt_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error_code TEXT NULL,
    UNIQUE (merchant_id, event_id),
    FOREIGN KEY (merchant_id, estabelecimento_id) REFERENCES ifood_connections(merchant_id, estabelecimento_id),
    CHECK ((state = 'processing' AND lease_id IS NOT NULL AND lease_until_utc IS NOT NULL)
        OR (state <> 'processing' AND lease_id IS NULL AND lease_until_utc IS NULL))
);

CREATE TABLE IF NOT EXISTS ifood_order_snapshots (
    merchant_id UUID NOT NULL,
    external_order_id UUID NOT NULL,
    estabelecimento_id UUID NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1 CHECK (revision > 0),
    order_type TEXT NOT NULL CHECK (btrim(order_type) <> ''),
    delivered_by TEXT NULL,
    remote_status TEXT NULL,
    preparation_state TEXT NOT NULL DEFAULT 'awaiting_acceptance'
        CHECK (preparation_state IN ('awaiting_acceptance','accepted','preparing','ready','not_applicable')),
    synchronization_state TEXT NOT NULL DEFAULT 'review'
        CHECK (synchronization_state IN ('synced','pending','uncertain','error','review')),
    currency TEXT NULL,
    order_amount NUMERIC(18,2) NULL CHECK (order_amount >= 0),
    prepaid_amount NUMERIC(18,2) NULL CHECK (prepaid_amount >= 0),
    pending_amount NUMERIC(18,2) NULL CHECK (pending_amount >= 0),
    scheduled_start_utc TIMESTAMPTZ NULL,
    scheduled_end_utc TIMESTAMPTZ NULL,
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (merchant_id, external_order_id),
    FOREIGN KEY (merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_order_links(merchant_id, external_order_id, estabelecimento_id),
    CHECK ((scheduled_start_utc IS NULL AND scheduled_end_utc IS NULL)
        OR (scheduled_start_utc IS NOT NULL AND scheduled_end_utc IS NOT NULL AND scheduled_end_utc >= scheduled_start_utc))
);

CREATE TABLE IF NOT EXISTS ifood_outbound_commands (
    operation_id UUID PRIMARY KEY CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
    merchant_id UUID NOT NULL,
    external_order_id UUID NOT NULL,
    estabelecimento_id UUID NOT NULL,
    command_type TEXT NOT NULL CHECK (btrim(command_type) <> ''),
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    payload_hash TEXT NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    expected_order_revision BIGINT NOT NULL CHECK (expected_order_revision > 0),
    actor_user_id INTEGER NULL,
    actor_motoboy_id INTEGER NULL,
    actor_session_id UUID NULL,
    parent_operation_id UUID NULL,
    state TEXT NOT NULL DEFAULT 'queued' CHECK (state IN ('queued','processing','uncertain','succeeded','rejected','review')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    lease_id UUID NULL,
    lease_until_utc TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    next_attempt_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finished_at_utc TIMESTAMPTZ NULL,
    last_error_code TEXT NULL,
    result JSONB NULL CHECK (result IS NULL OR jsonb_typeof(result) = 'object'),
    UNIQUE (operation_id, merchant_id, external_order_id, estabelecimento_id),
    FOREIGN KEY (merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_order_links(merchant_id, external_order_id, estabelecimento_id),
    FOREIGN KEY (parent_operation_id, merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_outbound_commands(operation_id, merchant_id, external_order_id, estabelecimento_id),
    CHECK (parent_operation_id IS NULL OR parent_operation_id <> operation_id),
    CHECK ((state = 'processing' AND lease_id IS NOT NULL AND lease_until_utc IS NOT NULL)
        OR (state <> 'processing' AND lease_id IS NULL AND lease_until_utc IS NULL))
);

CREATE TABLE IF NOT EXISTS ifood_disputes (
    merchant_id UUID NOT NULL,
    dispute_id UUID NOT NULL CHECK (dispute_id <> '00000000-0000-0000-0000-000000000000'),
    external_order_id UUID NOT NULL,
    estabelecimento_id UUID NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    timeout_action TEXT NOT NULL CHECK (btrim(timeout_action) <> ''),
    state TEXT NOT NULL DEFAULT 'open' CHECK (state IN ('open','responded','resolved','expired','review')),
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    response_operation_id UUID NULL,
    settlement JSONB NULL CHECK (settlement IS NULL OR jsonb_typeof(settlement) = 'object'),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (merchant_id, dispute_id),
    FOREIGN KEY (merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_order_links(merchant_id, external_order_id, estabelecimento_id),
    FOREIGN KEY (response_operation_id, merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_outbound_commands(operation_id, merchant_id, external_order_id, estabelecimento_id)
);

CREATE INDEX IF NOT EXISTS ix_ifood_connections_store ON ifood_connections(estabelecimento_id);
CREATE INDEX IF NOT EXISTS ix_ifood_order_links_display ON ifood_order_links(estabelecimento_id, display_number);
CREATE INDEX IF NOT EXISTS ix_ifood_inbox_pending ON ifood_events_inbox(next_attempt_at_utc, id) WHERE state = 'queued';
CREATE INDEX IF NOT EXISTS ix_ifood_inbox_lease ON ifood_events_inbox(lease_until_utc) WHERE state = 'processing';
CREATE INDEX IF NOT EXISTS ix_ifood_commands_pending ON ifood_outbound_commands(next_attempt_at_utc, created_at_utc) WHERE state = 'queued';
CREATE INDEX IF NOT EXISTS ix_ifood_commands_reconciliation ON ifood_outbound_commands(updated_at_utc) WHERE state = 'uncertain';
CREATE INDEX IF NOT EXISTS ix_ifood_commands_lease ON ifood_outbound_commands(lease_until_utc) WHERE state = 'processing';
CREATE INDEX IF NOT EXISTS ix_ifood_disputes_open ON ifood_disputes(estabelecimento_id, expires_at_utc) WHERE state = 'open';

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261013_01_ifood_integration_schema') ON CONFLICT (version) DO NOTHING;
COMMIT;
