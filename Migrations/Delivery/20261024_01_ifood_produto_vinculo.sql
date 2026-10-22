-- Vinculo entre o item do pedido iFood (items[].id) e o produto do cardapio da loja.
-- So acrescenta dados de exibicao (foto, categoria, aviso de bag): valores, quantidades e totais do pedido continuam 100% do
-- snapshot oficial, e o JSON do snapshot nunca e reescrito. Item sem vinculo continua valido.
-- Aditiva e idempotente. Rollback em rollback/20261024_01_ifood_produto_vinculo.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('ifood_connections') IS NULL THEN
        RAISE EXCEPTION 'ifood_produto_vinculo requer a integracao iFood (20261013_01_ifood_integration_schema)';
    END IF;
    IF to_regclass('cardapio_produto') IS NULL THEN
        RAISE EXCEPTION 'ifood_produto_vinculo requer o cardapio (20261001_01_cardapio_schema)';
    END IF;
END $$;

-- Alvo da FK composta: garante que o produto vinculado e da mesma loja do vinculo.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_produto_id_estab ON cardapio_produto (id, id_estabelecimento);

CREATE TABLE IF NOT EXISTS ifood_produto_vinculo (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    estabelecimento_id  UUID NOT NULL,
    merchant_id         UUID NOT NULL,
    -- 'complemento' fica reservado: esta fase so grava 'item'.
    tipo                TEXT NOT NULL DEFAULT 'item' CHECK (tipo IN ('item', 'complemento')),
    chave_id            TEXT NOT NULL CHECK (btrim(chave_id) <> ''),
    external_code       TEXT NULL,
    nome_ifood          TEXT NOT NULL CHECK (btrim(nome_ifood) <> ''),
    imagem_url          TEXT NULL,
    produto_id          UUID NULL,
    estado              TEXT NOT NULL DEFAULT 'pendente' CHECK (estado IN ('pendente', 'confirmado', 'desfeito')),
    origem              TEXT NULL CHECK (origem IN ('nome', 'manual')),
    pedidos_count       INTEGER NOT NULL DEFAULT 0 CHECK (pedidos_count >= 0),
    visto_primeira_em   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    visto_ultima_em     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    confirmado_por      INTEGER NULL,
    confirmado_em       TIMESTAMPTZ NULL,
    desfeito_em         TIMESTAMPTZ NULL,
    desfeito_por        INTEGER NULL,
    CONSTRAINT ux_ifood_produto_vinculo_chave UNIQUE (estabelecimento_id, merchant_id, tipo, chave_id),
    CONSTRAINT fk_ifood_produto_vinculo_conexao FOREIGN KEY (merchant_id, estabelecimento_id)
        REFERENCES ifood_connections (merchant_id, estabelecimento_id),
    CONSTRAINT fk_ifood_produto_vinculo_produto FOREIGN KEY (produto_id, estabelecimento_id)
        REFERENCES cardapio_produto (id, id_estabelecimento),
    CONSTRAINT ck_ifood_produto_vinculo_confirmado CHECK (estado <> 'confirmado' OR (produto_id IS NOT NULL AND origem IS NOT NULL)),
    -- Pendente e desfeito nunca apontam para produto: so o confirmado liga.
    CONSTRAINT ck_ifood_produto_vinculo_sem_produto CHECK (estado = 'confirmado' OR produto_id IS NULL)
);

-- Lista de pendentes (e desfeitos) da loja, mais recentes primeiro.
CREATE INDEX IF NOT EXISTS ix_ifood_produto_vinculo_pendentes
    ON ifood_produto_vinculo (estabelecimento_id, visto_ultima_em DESC, id)
    WHERE tipo = 'item' AND estado IN ('pendente', 'desfeito');
-- Vinculos de um produto (tela do produto e FK).
CREATE INDEX IF NOT EXISTS ix_ifood_produto_vinculo_produto
    ON ifood_produto_vinculo (produto_id) WHERE produto_id IS NOT NULL;
-- JOIN de leitura (fila e detalhe): so o confirmado entra.
CREATE INDEX IF NOT EXISTS ix_ifood_produto_vinculo_leitura
    ON ifood_produto_vinculo (estabelecimento_id, merchant_id, chave_id)
    WHERE tipo = 'item' AND estado = 'confirmado';
-- Reavaliacao ao criar/renomear produto: casa pendentes pelo nome normalizado.
CREATE INDEX IF NOT EXISTS ix_ifood_produto_vinculo_nome_pendente
    ON ifood_produto_vinculo (estabelecimento_id, (lower(btrim(regexp_replace(nome_ifood, '\s+', ' ', 'g')))))
    WHERE tipo = 'item' AND estado = 'pendente';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261024_01_ifood_produto_vinculo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
