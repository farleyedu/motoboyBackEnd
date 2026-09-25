BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 3 (ficha de atendimento): o que o atendente (e, depois, a IA) precisa saber de cada produto
-- alem de nome e preco: como o cliente chama o produto, instrucoes, restricoes e tempo extra.
-- Fica em TABELA PROPRIA e nao em colunas de cardapio_produto: varias consultas do cardapio
-- enumeram as colunas do produto, e acrescentar coluna ali quebraria o cardapio inteiro num deploy
-- fora de ordem. Aqui so o codigo novo le, e ele tolera a tabela ausente.
-- Aditiva e idempotente.
-- ---------------------------------------------------------------------------

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
VALUES ('20260926_01_produto_atendimento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
