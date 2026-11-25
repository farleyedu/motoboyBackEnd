-- Chamados: a loja pede ajuda pelo painel (botao Ajuda) e a Gestao responde em /apps/gestao/chamados.
-- Prazo de resposta: 4 h quando "algo parou", 24 h nos demais. Aditiva e idempotente.
-- Rollback em rollback/20261028_03_chamados.down.sql.
BEGIN;

CREATE TABLE IF NOT EXISTS chamado (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    aberto_por         INTEGER NULL,
    categoria          TEXT NOT NULL CHECK (categoria IN ('parou', 'duvida', 'pagamento', 'ideia')),
    titulo             TEXT NOT NULL,
    status             TEXT NOT NULL DEFAULT 'aberto' CHECK (status IN ('aberto', 'andamento', 'loja', 'resolvido')),
    prazo_em           TIMESTAMPTZ NOT NULL,
    primeira_resposta_em TIMESTAMPTZ NULL,
    resolvido_em       TIMESTAMPTZ NULL,
    nota               SMALLINT NULL CHECK (nota BETWEEN 1 AND 5),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_chamado_status ON chamado (status, prazo_em);
CREATE INDEX IF NOT EXISTS ix_chamado_loja ON chamado (estabelecimento_id, criado_em DESC);

CREATE TABLE IF NOT EXISTS chamado_mensagem (
    id         BIGSERIAL PRIMARY KEY,
    chamado_id BIGINT NOT NULL REFERENCES chamado (id) ON DELETE CASCADE,
    autor_id   INTEGER NULL,
    da_zippy   BOOLEAN NOT NULL DEFAULT FALSE,
    texto      TEXT NOT NULL,
    criado_em  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_chamado_mensagem ON chamado_mensagem (chamado_id, criado_em);

COMMIT;
