BEGIN;

-- Solicitações de vínculo feitas pelo próprio motoboy. O vínculo efetivo só é criado
-- depois da aprovação do restaurante.
CREATE TABLE IF NOT EXISTS motoboy_link_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy (id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    status TEXT NOT NULL DEFAULT 'pending',
    requested_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    reviewed_at_utc TIMESTAMPTZ NULL,
    reviewed_by_user_id INTEGER NULL REFERENCES usuario (id),
    rejection_reason TEXT NULL,
    CONSTRAINT ck_motoboy_link_request_status
        CHECK (status IN ('pending', 'approved', 'rejected'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_link_request_pending
    ON motoboy_link_requests (motoboy_id, estabelecimento_id)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS ix_motoboy_link_request_establishment
    ON motoboy_link_requests (estabelecimento_id, status, requested_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_motoboy_link_request_motoboy
    ON motoboy_link_requests (motoboy_id, requested_at_utc DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261006_05_motoboy_vinculo_solicitacoes')
ON CONFLICT (version) DO NOTHING;

COMMIT;
