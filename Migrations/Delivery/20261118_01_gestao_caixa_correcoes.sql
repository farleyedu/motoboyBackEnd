-- Gestao, 6C2-B: correcoes do Caixa. Solicitacao (do operador ou do gerente) de estorno de movimento ou de correcao do fundo,
-- decisao do gerente e historico do fundo corrigido. O dinheiro vive no livro; aqui ficam o pedido, a decisao e o antes -> depois.
-- Nada se apaga: a solicitacao so sai de 'pendente' uma vez; o fundo original (gestao_caixa_dias.fundo) nunca muda.
-- Requer 20261110_01_gestao_caixa.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_caixa_solicitacoes (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    chave varchar(100) NOT NULL,
    dia date NOT NULL,
    tipo varchar(20) NOT NULL CHECK (tipo IN ('estorno_movimento', 'corrigir_fundo')),
    acontecimento_id uuid,
    fundo_anterior numeric(15,2),
    fundo_novo numeric(15,2) CHECK (fundo_novo IS NULL OR fundo_novo >= 0),
    motivo varchar(300) NOT NULL CHECK (length(trim(motivo)) >= 3),
    status varchar(12) NOT NULL DEFAULT 'pendente' CHECK (status IN ('pendente', 'aprovada', 'rejeitada')),
    solicitado_por integer NOT NULL REFERENCES usuario(id),
    solicitado_em timestamptz NOT NULL,
    decidido_por integer REFERENCES usuario(id),
    decidido_em timestamptz,
    motivo_decisao varchar(300),
    correcao_id uuid,
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia),
    CHECK ((tipo = 'estorno_movimento') = (acontecimento_id IS NOT NULL)),
    CHECK ((tipo = 'corrigir_fundo') = (fundo_novo IS NOT NULL AND fundo_anterior IS NOT NULL)),
    CHECK ((status = 'pendente') = (decidido_em IS NULL AND decidido_por IS NULL)),
    CHECK (status <> 'rejeitada' OR length(trim(coalesce(motivo_decisao, ''))) >= 3),
    CHECK (status <> 'aprovada' OR correcao_id IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_solic_chave ON gestao_caixa_solicitacoes (estabelecimento_id, chave);
-- Um pedido pendente por movimento e um aprovado por movimento (barra pedido duplicado e estorno repetido).
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_solic_mov_pendente ON gestao_caixa_solicitacoes (estabelecimento_id, acontecimento_id)
    WHERE status = 'pendente' AND tipo = 'estorno_movimento';
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_solic_mov_aprovada ON gestao_caixa_solicitacoes (estabelecimento_id, acontecimento_id)
    WHERE status = 'aprovada' AND tipo = 'estorno_movimento';
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_solic_fundo_pendente ON gestao_caixa_solicitacoes (estabelecimento_id, dia)
    WHERE status = 'pendente' AND tipo = 'corrigir_fundo';
CREATE INDEX IF NOT EXISTS ix_caixa_solic_dia ON gestao_caixa_solicitacoes (estabelecimento_id, dia, solicitado_em);

CREATE TABLE IF NOT EXISTS gestao_caixa_fundo_ajustes (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    chave varchar(100) NOT NULL,
    dia date NOT NULL,
    ordem integer NOT NULL CHECK (ordem >= 1),
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    fundo_anterior numeric(15,2) NOT NULL,
    fundo_novo numeric(15,2) NOT NULL CHECK (fundo_novo >= 0),
    motivo varchar(300) NOT NULL CHECK (length(trim(motivo)) >= 3),
    acontecimento_id uuid NOT NULL,
    solicitacao_id uuid REFERENCES gestao_caixa_solicitacoes(id),
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia),
    UNIQUE (estabelecimento_id, dia, ordem),
    UNIQUE (estabelecimento_id, chave)
);

DROP TRIGGER IF EXISTS gestao_caixa_fundo_ajustes_imutavel ON gestao_caixa_fundo_ajustes;
CREATE TRIGGER gestao_caixa_fundo_ajustes_imutavel BEFORE UPDATE OR DELETE ON gestao_caixa_fundo_ajustes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

-- A solicitacao so muda uma vez, de pendente para decidida; o pedido em si (quem, o que, por que) nunca muda.
CREATE OR REPLACE FUNCTION gestao_caixa_solicitacao_guarda() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.status <> 'pendente'
       OR NEW.id <> OLD.id OR NEW.estabelecimento_id <> OLD.estabelecimento_id OR NEW.chave <> OLD.chave OR NEW.dia <> OLD.dia
       OR NEW.tipo <> OLD.tipo OR NEW.acontecimento_id IS DISTINCT FROM OLD.acontecimento_id
       OR NEW.fundo_anterior IS DISTINCT FROM OLD.fundo_anterior OR NEW.fundo_novo IS DISTINCT FROM OLD.fundo_novo
       OR NEW.motivo <> OLD.motivo OR NEW.solicitado_por <> OLD.solicitado_por OR NEW.solicitado_em <> OLD.solicitado_em THEN
        RAISE EXCEPTION 'gestao: % em % nao e permitido; a solicitacao so e decidida uma vez', TG_OP, TG_TABLE_NAME USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS gestao_caixa_solicitacoes_guarda ON gestao_caixa_solicitacoes;
CREATE TRIGGER gestao_caixa_solicitacoes_guarda BEFORE UPDATE OR DELETE ON gestao_caixa_solicitacoes
    FOR EACH ROW EXECUTE FUNCTION gestao_caixa_solicitacao_guarda();

COMMIT;
