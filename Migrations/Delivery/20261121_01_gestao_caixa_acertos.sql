-- Gestao, 6C5: acerto do motoboy no Caixa. O lote guarda quem acertou, quando, o que o motoboy entregou, o esperado e a diferenca;
-- os itens dizem quais pedidos entraram (cada pedido entra em um lote so). O dinheiro vive no livro: o lote liquida as vendas
-- "a receber com o motoboy" para a gaveta e lanca a diferenca (quebra ou sobra). Correcao e um lote novo que aponta o original;
-- o original nao muda. Requer 20261110_01_gestao_caixa.sql. Aditiva e idempotente.
BEGIN;

-- Conta recebivel "Dinheiro com motoboys" (subtipo novo): o dinheiro da entrega espera o acerto nela.
ALTER TABLE gestao_contas DROP CONSTRAINT IF EXISTS gestao_contas_subtipo_check;
ALTER TABLE gestao_contas ADD CONSTRAINT gestao_contas_subtipo_check CHECK (subtipo IN ('ifood', 'maquininha', 'motoboys', 'outro'));

CREATE TABLE IF NOT EXISTS gestao_caixa_acertos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    numero integer NOT NULL,
    chave varchar(120) NOT NULL,
    hash_conteudo varchar(64) NOT NULL,
    dia date NOT NULL,
    motoboy_id integer NOT NULL,
    tipo varchar(10) NOT NULL DEFAULT 'acerto' CHECK (tipo IN ('acerto', 'correcao')),
    corrige_id uuid REFERENCES gestao_caixa_acertos (id),
    esperado numeric(15,2) NOT NULL CHECK (esperado >= 0),
    entregue numeric(15,2) NOT NULL CHECK (entregue >= 0),
    diferenca numeric(15,2) NOT NULL,
    motivo varchar(300),
    situacao varchar(10) NOT NULL CHECK (situacao IN ('conferido', 'parcial')),
    pendentes_depois integer NOT NULL DEFAULT 0 CHECK (pendentes_depois >= 0),
    diferenca_id uuid,
    operador integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia),
    CHECK (diferenca = 0 OR length(trim(coalesce(motivo, ''))) >= 3),
    CHECK ((tipo = 'correcao') = (corrige_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_acerto_chave ON gestao_caixa_acertos (estabelecimento_id, chave);
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_acerto_numero ON gestao_caixa_acertos (estabelecimento_id, numero);
CREATE INDEX IF NOT EXISTS ix_caixa_acerto_dia ON gestao_caixa_acertos (estabelecimento_id, dia, em);
CREATE INDEX IF NOT EXISTS ix_caixa_acerto_motoboy ON gestao_caixa_acertos (estabelecimento_id, motoboy_id, em);

CREATE TABLE IF NOT EXISTS gestao_caixa_acerto_itens (
    acerto_id uuid NOT NULL REFERENCES gestao_caixa_acertos (id),
    estabelecimento_id uuid NOT NULL,
    pedido_id integer NOT NULL,
    venda_id uuid NOT NULL,
    dinheiro numeric(15,2) NOT NULL DEFAULT 0,
    digital_integracao numeric(15,2) NOT NULL DEFAULT 0,
    cartao_manual numeric(15,2) NOT NULL DEFAULT 0,
    ifood numeric(15,2) NOT NULL DEFAULT 0,
    -- Cartao passado na maquininha da loja pelo motoboy: comprovante fisico nao confirma sozinho; o caixa confere e marca.
    conferido_manual boolean NOT NULL DEFAULT false,
    liquidacoes uuid[] NOT NULL DEFAULT '{}',
    PRIMARY KEY (acerto_id, pedido_id)
);
-- Cada pedido entra em um lote so: duas telas (ou um reenvio) nao acertam o mesmo pedido duas vezes.
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_acerto_item_pedido ON gestao_caixa_acerto_itens (estabelecimento_id, pedido_id);

COMMIT;
