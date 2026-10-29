-- Gestao, Integracao 6B: o que o Estoque guardava so no navegador. Minimo de cada insumo, motivos de saida da loja (e quais
-- motivos do Zippy estao desligados) e o rascunho da contagem em andamento. Sao configuracao da loja, nao livro: podem mudar.
-- O historico continua so no livro (gestao_acontecimentos). Requer 20261105_01_gestao_fundacao.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_estoque_minimos (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    item_id uuid NOT NULL,
    minimo numeric(18,4) NOT NULL CHECK (minimo > 0),
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, item_id)
);

-- id textual: os motivos do Zippy tem id fixo ('vencimento', 'cortesia'...) e aqui so guardam ativo/desativado.
CREATE TABLE IF NOT EXISTS gestao_estoque_motivos (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    id varchar(40) NOT NULL,
    padrao boolean NOT NULL DEFAULT false,
    rotulo varchar(80) CHECK (padrao OR length(trim(coalesce(rotulo, ''))) > 0),
    natureza varchar(6) CHECK (padrao OR natureza IN ('perda', 'uso')),
    cor varchar(9),
    ativo boolean NOT NULL DEFAULT true,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, id)
);

-- Um rascunho por loja (so o que foi contado e explicado; nunca o esperado, para a contagem as cegas continuar cega).
CREATE TABLE IF NOT EXISTS gestao_estoque_contagem_rascunho (
    estabelecimento_id uuid PRIMARY KEY REFERENCES estabelecimentos(id),
    dados jsonb NOT NULL,
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now()
);

COMMIT;
