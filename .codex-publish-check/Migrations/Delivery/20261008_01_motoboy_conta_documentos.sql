BEGIN;
ALTER TABLE motoboy ADD COLUMN IF NOT EXISTS ano_moto INTEGER NULL;
ALTER TABLE motoboy_active_sessions ADD COLUMN IF NOT EXISTS paused_at_utc TIMESTAMPTZ NULL;
CREATE TABLE IF NOT EXISTS motoboy_documentos (
    id UUID NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id) ON DELETE CASCADE,
    tipo TEXT NOT NULL CHECK (tipo IN ('avatar', 'identificacao', 'cnh', 'moto')),
    conteudo BYTEA NOT NULL CHECK (octet_length(conteudo) <= 4194304),
    enviado_em_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (motoboy_id, tipo)
);
INSERT INTO delivery_tracking_schema_versions (version) VALUES ('20261008_01_motoboy_conta_documentos') ON CONFLICT DO NOTHING;
COMMIT;
