-- Interesse da loja: registro genérico (loja, pessoa e papel, tipo, quando, contexto), para a Gestão saber quem quis
-- comprar mais motoboys. O que já está nas tabelas de compra não é copiado: só é lido. Aditiva e idempotente.
-- Rollback em rollback/20261212_03_loja_interesse.down.sql (apaga o histórico, que não se recupera).
BEGIN;

CREATE TABLE IF NOT EXISTS loja_interesse (
    id                  BIGSERIAL PRIMARY KEY,
    estabelecimento_id  UUID NOT NULL,
    pessoa_id           INTEGER NULL,
    pessoa_papel        TEXT NULL,
    tipo                TEXT NOT NULL CHECK (length(tipo) BETWEEN 1 AND 60),
    quando              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    contexto            JSONB NOT NULL DEFAULT '{}'::jsonb,
    -- Evita repetir o mesmo fato (ex.: o mesmo motoboy barrado várias vezes no mesmo turno). Nula = sempre grava.
    chave_unica         TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_loja_interesse_tipo ON loja_interesse (tipo, quando DESC);
CREATE INDEX IF NOT EXISTS ix_loja_interesse_loja ON loja_interesse (estabelecimento_id, quando DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_loja_interesse_chave
    ON loja_interesse (estabelecimento_id, chave_unica) WHERE chave_unica IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261212_03_loja_interesse')
ON CONFLICT (version) DO NOTHING;

COMMIT;
