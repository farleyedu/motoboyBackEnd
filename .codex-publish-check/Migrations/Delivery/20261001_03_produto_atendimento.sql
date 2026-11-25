-- Cria cardapio_produto_atendimento de verdade agora que cardapio_produto existe
-- (20261001_01_cardapio_schema.sql). A tentativa original (20260926_01_produto_atendimento.sql) ja
-- ficou marcada como aplicada mesmo pulando a criacao (guarda condicional, cardapio_produto nao
-- existia ainda) -- ela nao vai rodar de novo sozinha, por isso esta e' uma versao nova.
-- Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS cardapio_produto_atendimento (
    produto_id              UUID PRIMARY KEY REFERENCES cardapio_produto (id) ON DELETE CASCADE,
    id_estabelecimento      UUID NOT NULL,
    apelidos                TEXT[] NOT NULL DEFAULT '{}',
    instrucoes              TEXT NULL,
    restricoes              TEXT NULL,
    tempo_extra_preparo_min INTEGER NULL
        CHECK (tempo_extra_preparo_min IS NULL OR tempo_extra_preparo_min BETWEEN 0 AND 240),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_cardapio_produto_atendimento_estab
    ON cardapio_produto_atendimento (id_estabelecimento);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261001_03_produto_atendimento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
