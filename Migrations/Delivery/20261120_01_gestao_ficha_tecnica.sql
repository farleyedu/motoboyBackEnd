-- 6C3-A: ficha tecnica real (por produto do cardapio e loja) e de-para de vendas do cardapio.
-- Aditiva e idempotente. Nao move estoque e nao grava acontecimento: so cadastro. Rollback em rollback/20261120_01_gestao_ficha_tecnica.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('cardapio_produto') IS NULL THEN RAISE EXCEPTION 'ficha tecnica requer o cardapio (20261001_01_cardapio_schema)'; END IF;
    IF to_regclass('insumo') IS NULL THEN RAISE EXCEPTION 'ficha tecnica requer os ingredientes (Inteligencia/20261019_01_ingredientes)'; END IF;
END $$;

-- Alvo das FKs compostas: produto e insumo sempre da mesma loja da ficha.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_produto_id_estab ON cardapio_produto (id, id_estabelecimento);

-- Um componente por linha: quanto de um insumo entra em UMA unidade vendida do produto.
-- modalidades NULL = vale para todas; embalagem de delivery lista so {delivery}.
CREATE TABLE IF NOT EXISTS gestao_ficha_componente (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    estabelecimento_id uuid NOT NULL,
    produto_id         uuid NOT NULL,
    insumo_id          uuid NOT NULL,
    quantidade         numeric(18,4) NOT NULL CHECK (quantidade > 0),
    unidade            varchar(2) NOT NULL CHECK (unidade IN ('g', 'ml', 'un')),
    modalidades        text[] NULL CHECK (modalidades IS NULL
        OR (cardinality(modalidades) > 0 AND modalidades <@ ARRAY['salao', 'retirada', 'delivery']::text[])),
    ordem              integer NOT NULL DEFAULT 0,
    atualizado_por     integer NULL,
    atualizado_em      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_gestao_ficha_produto FOREIGN KEY (produto_id, estabelecimento_id)
        REFERENCES cardapio_produto (id, id_estabelecimento),
    CONSTRAINT fk_gestao_ficha_insumo FOREIGN KEY (estabelecimento_id, insumo_id)
        REFERENCES insumo (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_ficha_produto ON gestao_ficha_componente (estabelecimento_id, produto_id, ordem);
-- O mesmo insumo nao entra duas vezes no mesmo grupo de modalidades do produto.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_ficha_insumo
    ON gestao_ficha_componente (estabelecimento_id, produto_id, insumo_id, (coalesce(array_to_string(modalidades, ','), '')));

-- De-para de vendas do cardapio: item de pedido sem produto (linha avulsa) -> produto vendido. iFood continua em ifood_produto_vinculo.
CREATE TABLE IF NOT EXISTS gestao_venda_vinculo (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    estabelecimento_id uuid NOT NULL,
    canal              text NOT NULL CHECK (canal IN ('cardapio')),
    nome_chave         text NOT NULL CHECK (btrim(nome_chave) <> ''),
    nome               text NOT NULL,
    produto_id         uuid NOT NULL,
    confirmado_por     integer NULL,
    confirmado_em      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_gestao_venda_vinculo UNIQUE (estabelecimento_id, canal, nome_chave),
    CONSTRAINT fk_gestao_venda_vinculo_produto FOREIGN KEY (produto_id, estabelecimento_id)
        REFERENCES cardapio_produto (id, id_estabelecimento)
);

COMMIT;
