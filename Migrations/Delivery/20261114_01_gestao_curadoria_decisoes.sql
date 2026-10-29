-- Gestao, Integracao 6B: decisoes da curadoria Zippy (aprovar, observar, corrigir, rejeitar, reabrir).
-- Regra 2: decisao e um acontecimento imutavel. Nada de UPDATE/DELETE/TRUNCATE; o estado do caso sai da ultima decisao
-- e reabrir e uma decisao nova que aponta a anterior (corrige_id). Regra 6: a decisao mexe so no catalogo global; o
-- vinculo de cada loja continua apontando para o mesmo insumo.
-- A decisao e da Zippy (super admin), nao de uma loja: por isso nao vai para gestao_acontecimentos (que exige loja).
-- Requer 20261105_01_gestao_fundacao.sql (gestao_bloqueia_alteracao) e 20261107_01_gestao_vinculos.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_curadoria_decisoes (
    id uuid PRIMARY KEY,
    -- Caso gerado dos sinais das lojas: prod:<global>, texto:<nome_chave>, dup:<global>:<global>, cat:<chave>.
    caso_id varchar(220) NOT NULL CHECK (length(caso_id) > 0),
    -- Foto do caso no momento da decisao (o caso muda com os sinais; o registro nao).
    caso_nome varchar(300) NOT NULL,
    caso_tipo varchar(10) NOT NULL CHECK (caso_tipo IN ('depara', 'prod', 'contra', 'dup', 'cat', 'inc', 'rec')),
    decisao varchar(10) NOT NULL CHECK (decisao IN ('aprovar', 'observar', 'corrigir', 'rejeitar', 'reabrir')),
    responsavel_id integer NOT NULL REFERENCES usuario(id),
    responsavel_nome varchar(120) NOT NULL CHECK (length(trim(responsavel_nome)) > 0),
    motivo varchar(500) NOT NULL CHECK (length(trim(motivo)) > 0),
    em timestamptz NOT NULL,
    anterior varchar(300) NOT NULL,
    novo varchar(300) NOT NULL,
    lote uuid,
    corrige_id uuid REFERENCES gestao_curadoria_decisoes(id),
    excecao boolean NOT NULL DEFAULT false,
    confirmacoes_no_registro integer,
    -- Produto global escolhido (aprovar o mais aceito, corrigir, mesclar).
    produto_global_id uuid REFERENCES gestao_catalogo_global(id),
    -- O que a decisao fez no catalogo, para a reabertura desfazer: status anterior, vinculos movidos na mescla.
    efeito jsonb NOT NULL DEFAULT '{}'::jsonb,
    criado_em timestamptz NOT NULL DEFAULT now(),
    CHECK ((decisao = 'reabrir') = (corrige_id IS NOT NULL)),
    CHECK (decisao <> 'observar' OR confirmacoes_no_registro IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_gestao_curadoria_decisoes_caso ON gestao_curadoria_decisoes (caso_id, em, id);
CREATE INDEX IF NOT EXISTS ix_gestao_curadoria_decisoes_em ON gestao_curadoria_decisoes (em DESC);
-- Uma decisao so pode ser reaberta uma vez.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_curadoria_decisoes_reabre ON gestao_curadoria_decisoes (corrige_id) WHERE corrige_id IS NOT NULL;

DROP TRIGGER IF EXISTS gestao_curadoria_decisoes_imutavel ON gestao_curadoria_decisoes;
CREATE TRIGGER gestao_curadoria_decisoes_imutavel BEFORE UPDATE OR DELETE ON gestao_curadoria_decisoes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_curadoria_decisoes_sem_truncate ON gestao_curadoria_decisoes;
CREATE TRIGGER gestao_curadoria_decisoes_sem_truncate BEFORE TRUNCATE ON gestao_curadoria_decisoes
    FOR EACH STATEMENT EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
