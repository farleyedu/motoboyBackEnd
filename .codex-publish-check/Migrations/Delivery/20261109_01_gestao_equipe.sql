-- Gestao, Logica E4: equipe e remuneracao. Pessoa (regra de pagamento + escala recorrente) e turno (um por pessoa por dia).
-- O dinheiro vive no livro: turno realizado = acontecimento 'turno' (despesa a pagar); falta nao publica (estorna se ja tinha);
-- adiantamento = despesa paga, compensada depois por ajuste nos itens a pagar (gestao_equipe_compensacoes guarda quanto).
-- Pessoa e turno mudam de estado, mas nunca somem (DELETE barrado) e cada mudanca vira uma linha imutavel de historico.
-- Requer 20261105_01_gestao_fundacao.sql e 20261107_01_gestao_vinculos.sql (gestao_bloqueia_exclusao). Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_pessoas (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) > 0),
    funcao varchar(60) NOT NULL DEFAULT '',
    vinculo varchar(12) NOT NULL CHECK (vinculo IN ('fixo', 'diarista', 'freelancer', 'hora', 'motoboy', 'prestador')),
    regra jsonb NOT NULL,
    paga_com varchar(10) NOT NULL DEFAULT 'pix' CHECK (paga_com IN ('pix', 'dinheiro')),
    chave_pix varchar(120),
    usuario_id integer REFERENCES usuario(id),
    cor varchar(20),
    escala jsonb NOT NULL DEFAULT '[]'::jsonb,
    ativo boolean NOT NULL DEFAULT true,
    desde date NOT NULL,
    versao integer NOT NULL DEFAULT 1 CHECK (versao > 0),
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_pessoas_loja ON gestao_pessoas (estabelecimento_id, ativo, nome);

CREATE TABLE IF NOT EXISTS gestao_pessoas_historico (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    pessoa_id uuid NOT NULL,
    versao integer NOT NULL,
    retrato jsonb NOT NULL,
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    UNIQUE (pessoa_id, versao),
    FOREIGN KEY (estabelecimento_id, pessoa_id) REFERENCES gestao_pessoas (estabelecimento_id, id)
);

CREATE TABLE IF NOT EXISTS gestao_turnos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    pessoa_id uuid NOT NULL,
    data date NOT NULL,
    entrada varchar(5) NOT NULL,
    saida varchar(5) NOT NULL,
    status varchar(10) NOT NULL CHECK (status IN ('previsto', 'realizado', 'falta', 'cancelado')),
    avulso boolean NOT NULL DEFAULT false,
    justificada boolean NOT NULL DEFAULT false,
    entregas integer NOT NULL DEFAULT 0 CHECK (entregas >= 0),
    pagamento_avulso jsonb,
    adicionais jsonb NOT NULL DEFAULT '[]'::jsonb,
    base_comissao numeric(15,2) NOT NULL DEFAULT 0 CHECK (base_comissao >= 0),
    valor numeric(15,2) NOT NULL DEFAULT 0,
    previsto numeric(15,2) NOT NULL DEFAULT 0,
    itens jsonb NOT NULL DEFAULT '[]'::jsonb,
    -- Sobe a cada nova publicacao depois de um estorno (a origem do livro nao repete).
    revisao integer NOT NULL DEFAULT 0,
    acontecimento_id uuid,
    lancamento_id uuid,
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (estabelecimento_id, id),
    UNIQUE (estabelecimento_id, pessoa_id, data),
    FOREIGN KEY (estabelecimento_id, pessoa_id) REFERENCES gestao_pessoas (estabelecimento_id, id),
    CHECK ((acontecimento_id IS NULL) = (lancamento_id IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_gestao_turnos_data ON gestao_turnos (estabelecimento_id, data);

CREATE TABLE IF NOT EXISTS gestao_turnos_historico (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    turno_id uuid NOT NULL,
    em timestamptz NOT NULL,
    por integer REFERENCES usuario(id),
    acao varchar(12) NOT NULL,
    valor_anterior numeric(15,2),
    valor_novo numeric(15,2),
    motivo varchar(300),
    detalhe varchar(300),
    FOREIGN KEY (estabelecimento_id, turno_id) REFERENCES gestao_turnos (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_turnos_historico ON gestao_turnos_historico (estabelecimento_id, turno_id, em);

-- Quanto de cada adiantamento ja abateu de cada item a pagar (o ajuste fica no livro; aqui fica o vinculo entre os dois).
CREATE TABLE IF NOT EXISTS gestao_equipe_compensacoes (
    estabelecimento_id uuid NOT NULL,
    adiantamento_id uuid NOT NULL,
    lancamento_id uuid NOT NULL,
    valor numeric(15,2) NOT NULL CHECK (valor > 0),
    ajuste_id uuid NOT NULL,
    criado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, adiantamento_id, lancamento_id),
    FOREIGN KEY (estabelecimento_id, adiantamento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, ajuste_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id)
);

DROP TRIGGER IF EXISTS gestao_pessoas_sem_delete ON gestao_pessoas;
CREATE TRIGGER gestao_pessoas_sem_delete BEFORE DELETE ON gestao_pessoas FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_turnos_sem_delete ON gestao_turnos;
CREATE TRIGGER gestao_turnos_sem_delete BEFORE DELETE ON gestao_turnos FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_pessoas_historico_imutavel ON gestao_pessoas_historico;
CREATE TRIGGER gestao_pessoas_historico_imutavel BEFORE UPDATE OR DELETE ON gestao_pessoas_historico
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_turnos_historico_imutavel ON gestao_turnos_historico;
CREATE TRIGGER gestao_turnos_historico_imutavel BEFORE UPDATE OR DELETE ON gestao_turnos_historico
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_equipe_compensacoes_imutavel ON gestao_equipe_compensacoes;
CREATE TRIGGER gestao_equipe_compensacoes_imutavel BEFORE UPDATE OR DELETE ON gestao_equipe_compensacoes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
