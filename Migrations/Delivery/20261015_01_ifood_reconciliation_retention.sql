BEGIN;

-- IF-16/IF-17: conciliação auditada de pedidos em revisão e retenção configurável pela tela.
-- Nada é ligado por esta migration: a limpeza automática começa DESLIGADA.
ALTER TABLE ifood_app_settings ADD COLUMN IF NOT EXISTS retention_enabled BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE ifood_app_settings ADD COLUMN IF NOT EXISTS retention_days SMALLINT NOT NULL DEFAULT 30;
DO $$ BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='ck_ifood_settings_retention_days') THEN
        ALTER TABLE ifood_app_settings ADD CONSTRAINT ck_ifood_settings_retention_days CHECK (retention_days BETWEEN 7 AND 365);
    END IF;
END $$;

-- Toda ação manual sobre eventos/pedidos em revisão fica registrada (quem, quando, anotação, estado anterior).
CREATE TABLE IF NOT EXISTS ifood_reconciliation_audit (
    id BIGSERIAL PRIMARY KEY,
    merchant_id UUID NOT NULL,
    estabelecimento_id UUID NOT NULL,
    external_order_id UUID NOT NULL,
    inbox_id BIGINT NULL,
    action TEXT NOT NULL CHECK (action IN ('requeue','dismiss','resolve_review')),
    actor_user_id INTEGER NULL,
    note TEXT NULL CHECK (note IS NULL OR char_length(note) <= 500),
    previous_state TEXT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (merchant_id, external_order_id, estabelecimento_id)
        REFERENCES ifood_order_links(merchant_id, external_order_id, estabelecimento_id)
);
CREATE INDEX IF NOT EXISTS ix_ifood_reconciliation_order ON ifood_reconciliation_audit(merchant_id, external_order_id, created_at_utc);

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261015_01_ifood_reconciliation_retention') ON CONFLICT DO NOTHING;
COMMIT;
