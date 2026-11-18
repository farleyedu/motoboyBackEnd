BEGIN;

-- Mesas, P02 (CONTRATO-API §1.2): ponto em tempo real da equipe. Entrada, pausas e saida com carimbo do servidor.
-- Um ponto aberto por pessoa; uma pausa aberta por ponto. Ao sair, o servico preenche gestao_turnos pelo caminho da Equipe
-- (sem apagar o que o gerente ja decidiu) e grava aqui o que aconteceu com o turno.
-- Requer 20261109_01_gestao_equipe.sql (gestao_pessoas, gestao_turnos, gestao_bloqueia_exclusao). Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS gestao_ponto (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    pessoa_id          UUID NOT NULL,
    -- Dia local da loja na hora da entrada: quem entra 22:00 e sai 02:00 fica no turno do dia da entrada.
    dia                DATE NOT NULL,
    modo               TEXT NOT NULL,
    entrada            TIMESTAMPTZ NOT NULL,
    entrada_por        INTEGER NULL,
    saida              TIMESTAMPTZ NULL,
    saida_por          INTEGER NULL,
    fechado_por        TEXT NULL,
    motivo_saida       TEXT NULL,
    -- O que o fechamento fez em gestao_turnos: preenchido, mantido (o gerente ja tinha decidido) ou nao_aplicado (aviso diz por que).
    turno_situacao     TEXT NULL,
    turno_entrada      VARCHAR(5) NULL,
    turno_saida        VARCHAR(5) NULL,
    turno_aviso        TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, pessoa_id) REFERENCES gestao_pessoas (estabelecimento_id, id)
);

ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_modo;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_modo CHECK (modo IN ('garcom', 'motoboy', 'cozinha', 'caixa'));
ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_fechado_por;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_fechado_por CHECK (fechado_por IS NULL OR fechado_por IN ('pessoa', 'gerente', 'sistema'));
ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_saida;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_saida CHECK (saida IS NULL OR saida > entrada);
ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_fechado;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_fechado CHECK ((saida IS NULL) = (fechado_por IS NULL));
ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_motivo;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_motivo CHECK (motivo_saida IS NULL OR length(motivo_saida) <= 280);
ALTER TABLE gestao_ponto DROP CONSTRAINT IF EXISTS ck_gestao_ponto_turno;
ALTER TABLE gestao_ponto ADD CONSTRAINT ck_gestao_ponto_turno
    CHECK (turno_situacao IS NULL OR turno_situacao IN ('preenchido', 'mantido', 'nao_aplicado'));

CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_ponto_aberto ON gestao_ponto (pessoa_id) WHERE saida IS NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_ponto_dia ON gestao_ponto (estabelecimento_id, dia, pessoa_id);
CREATE INDEX IF NOT EXISTS ix_gestao_ponto_abertos ON gestao_ponto (estabelecimento_id) WHERE saida IS NULL;

CREATE TABLE IF NOT EXISTS gestao_ponto_pausa (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL,
    ponto_id           UUID NOT NULL,
    inicio             TIMESTAMPTZ NOT NULL,
    fim                TIMESTAMPTZ NULL,
    motivo             TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (estabelecimento_id, ponto_id) REFERENCES gestao_ponto (estabelecimento_id, id)
);
ALTER TABLE gestao_ponto_pausa DROP CONSTRAINT IF EXISTS ck_gestao_ponto_pausa_fim;
ALTER TABLE gestao_ponto_pausa ADD CONSTRAINT ck_gestao_ponto_pausa_fim CHECK (fim IS NULL OR fim >= inicio);
ALTER TABLE gestao_ponto_pausa DROP CONSTRAINT IF EXISTS ck_gestao_ponto_pausa_motivo;
ALTER TABLE gestao_ponto_pausa ADD CONSTRAINT ck_gestao_ponto_pausa_motivo CHECK (motivo IS NULL OR length(motivo) <= 140);

CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_ponto_pausa_aberta ON gestao_ponto_pausa (ponto_id) WHERE fim IS NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_ponto_pausa_ponto ON gestao_ponto_pausa (ponto_id, inicio);

-- Ponto nunca some: e prova de jornada.
DROP TRIGGER IF EXISTS gestao_ponto_sem_delete ON gestao_ponto;
CREATE TRIGGER gestao_ponto_sem_delete BEFORE DELETE ON gestao_ponto FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_ponto_pausa_sem_delete ON gestao_ponto_pausa;
CREATE TRIGGER gestao_ponto_pausa_sem_delete BEFORE DELETE ON gestao_ponto_pausa FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261204_01_gestao_ponto')
ON CONFLICT (version) DO NOTHING;

COMMIT;
