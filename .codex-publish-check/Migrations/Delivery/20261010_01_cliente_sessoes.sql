BEGIN;
ALTER TABLE clientes ADD COLUMN IF NOT EXISTS endereco_historico_importado BOOLEAN NOT NULL DEFAULT FALSE;
CREATE TABLE IF NOT EXISTS cliente_sessoes (
    token_hash TEXT PRIMARY KEY,
    id_cliente UUID NOT NULL REFERENCES clientes(id) ON DELETE CASCADE,
    id_estabelecimento UUID NOT NULL,
    expira_em TIMESTAMPTZ NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_cliente_sessoes_expira ON cliente_sessoes(expira_em);
INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261010_01_cliente_sessoes') ON CONFLICT DO NOTHING;
COMMIT;
