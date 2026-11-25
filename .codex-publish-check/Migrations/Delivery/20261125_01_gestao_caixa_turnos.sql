-- Gestao, Caixa: abrir de novo depois de fechar. Fechado o caixa do dia, quem opera escolhe:
--   novo turno  -> o fechamento fica valendo (quebra/sobra e recolhimento no livro) e o dia abre de novo com outro fundo;
--   reabrir     -> o fechamento e desfeito (estorno da quebra/sobra, dos ajustes e do recolhimento: o dinheiro volta do cofre
--                  para a gaveta) e o caixa continua aberto de onde parou.
-- gestao_caixa_dias continua sendo uma linha por dia (o turno atual); cada fechamento que saiu dela vai, inteiro, para
-- gestao_caixa_turnos (nada se apaga). versao muda a cada saida e entra nas chaves de idempotencia do livro; ciclo_desde marca
-- desde quando valem as correcoes do fechamento atual; turno > 1 faz a fita e o fundo contarem so a partir da abertura do turno.
-- Requer 20261110_01_gestao_caixa.sql e 20261123_01_gestao_caixa_aprovacao.sql. Aditiva e idempotente.
BEGIN;

ALTER TABLE gestao_caixa_dias
    ADD COLUMN IF NOT EXISTS versao integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS turno integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS ciclo_desde timestamptz;

CREATE TABLE IF NOT EXISTS gestao_caixa_turnos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    dia date NOT NULL,
    versao integer NOT NULL,
    turno integer NOT NULL,
    destino varchar(12) NOT NULL CHECK (destino IN ('novo_turno', 'reaberto')),
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL,
    motivo varchar(300),
    estornos jsonb NOT NULL DEFAULT '[]'::jsonb,
    -- o fechamento como estava
    fundo numeric(15,2) NOT NULL,
    aberto_por integer NOT NULL,
    aberto_em timestamptz NOT NULL,
    ciclo_desde timestamptz,
    fechado_por integer NOT NULL,
    fechado_em timestamptz NOT NULL,
    fechamento_id uuid NOT NULL,
    recolhimento_id uuid,
    esperado numeric(15,2) NOT NULL,
    contado numeric(15,2) NOT NULL,
    maquininha jsonb,
    maquininha_esperado jsonb,
    justificativa varchar(300),
    fica numeric(15,2),
    cofre numeric(15,2),
    aprovado_por integer,
    UNIQUE (estabelecimento_id, dia, versao),
    FOREIGN KEY (estabelecimento_id, dia) REFERENCES gestao_caixa_dias (estabelecimento_id, dia),
    CHECK (destino = 'novo_turno' OR length(trim(coalesce(motivo, ''))) >= 3)
);
CREATE INDEX IF NOT EXISTS ix_gestao_caixa_turnos_dia ON gestao_caixa_turnos (estabelecimento_id, dia, versao);

DROP TRIGGER IF EXISTS gestao_caixa_turnos_imutavel ON gestao_caixa_turnos;
CREATE TRIGGER gestao_caixa_turnos_imutavel BEFORE UPDATE OR DELETE ON gestao_caixa_turnos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
