-- Gestao, Logica E5: caixa do dia. Gaveta e cofre sao contas 'caixa' (subtipo 'outro') do livro; gestao_caixas diz qual e qual.
-- O dinheiro vive no livro (abertura, sangria, suprimento, pagamento em dinheiro, devolucao, fechamento, recolhimento);
-- aqui ficam o dia (aberto/fechado), cada contagem as cegas e as correcoes do valor contado (nada se apaga).
-- Requer 20261105_01_gestao_fundacao.sql e 20261107_01_gestao_vinculos.sql (gestao_bloqueia_exclusao). Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_caixas (
    estabelecimento_id uuid PRIMARY KEY REFERENCES estabelecimentos(id),
    conta_gaveta_id uuid NOT NULL,
    conta_cofre_id uuid NOT NULL,
    criado_em timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (estabelecimento_id, conta_gaveta_id) REFERENCES gestao_contas (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, conta_cofre_id) REFERENCES gestao_contas (estabelecimento_id, id),
    CHECK (conta_gaveta_id <> conta_cofre_id)
);

CREATE TABLE IF NOT EXISTS gestao_caixa_dias (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    dia date NOT NULL,
    fundo numeric(15,2) NOT NULL CHECK (fundo >= 0),
    aberto_por integer NOT NULL REFERENCES usuario(id),
    aberto_em timestamptz NOT NULL,
    abertura_id uuid NOT NULL,
    fechado_por integer REFERENCES usuario(id),
    fechado_em timestamptz,
    fechamento_id uuid,
    recolhimento_id uuid,
    esperado numeric(15,2),
    contado numeric(15,2),
    maquininha jsonb,
    maquininha_esperado jsonb,
    justificativa varchar(300),
    fica numeric(15,2),
    cofre numeric(15,2),
    PRIMARY KEY (estabelecimento_id, dia),
    CHECK ((fechado_em IS NULL) = (fechamento_id IS NULL))
);

CREATE TABLE IF NOT EXISTS gestao_caixa_contagens (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    dia date NOT NULL,
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    notas jsonb NOT NULL,
    moedas numeric(15,2) NOT NULL,
    contado numeric(15,2) NOT NULL,
    esperado numeric(15,2) NOT NULL,
    etapa varchar(12) NOT NULL CHECK (etapa IN ('conferencia', 'fechamento')),
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia)
);
CREATE INDEX IF NOT EXISTS ix_gestao_caixa_contagens_dia ON gestao_caixa_contagens (estabelecimento_id, dia, em);

CREATE TABLE IF NOT EXISTS gestao_caixa_ajustes (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    dia date NOT NULL,
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    contado_anterior numeric(15,2) NOT NULL,
    contado_novo numeric(15,2) NOT NULL CHECK (contado_novo >= 0),
    motivo varchar(300) NOT NULL CHECK (length(trim(motivo)) >= 3),
    acontecimento_id uuid,
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia)
);

DROP TRIGGER IF EXISTS gestao_caixa_dias_sem_delete ON gestao_caixa_dias;
CREATE TRIGGER gestao_caixa_dias_sem_delete BEFORE DELETE ON gestao_caixa_dias FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_caixas_sem_delete ON gestao_caixas;
CREATE TRIGGER gestao_caixas_sem_delete BEFORE DELETE ON gestao_caixas FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_caixa_contagens_imutavel ON gestao_caixa_contagens;
CREATE TRIGGER gestao_caixa_contagens_imutavel BEFORE UPDATE OR DELETE ON gestao_caixa_contagens
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_caixa_ajustes_imutavel ON gestao_caixa_ajustes;
CREATE TRIGGER gestao_caixa_ajustes_imutavel BEFORE UPDATE OR DELETE ON gestao_caixa_ajustes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
