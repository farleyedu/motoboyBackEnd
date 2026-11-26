-- Turnos de entrega e créditos de motoboy (docs/assinatura/PROMPT-3-creditos.md no zippy-admin).
--   delivery_turno_entrega -> cada turno criado (o dia vira às 5h no fuso da loja; total nunca diminui no meio do turno).
--   delivery_turno_estado  -> como a loja estava na última avaliação, para reconstruir o intervalo sem reescrever o passado.
--   delivery_turno_credito -> um crédito gasto por motoboy em cada turno; turno nulo = saiu com a loja fechada, conta no próximo.
-- Reserva não é gravada: sai das paradas ativas. Sem chave estrangeira para estabelecimentos de propósito (a checagem
-- travaria a linha da loja fora de ordem). motoboy_vaga_periodo deixa de ser escrita e fica como histórico.
-- Aditiva e idempotente. Sem ela o servidor não limita créditos. Rollback em rollback/20261212_01_turno_entrega.down.sql.
BEGIN;

CREATE TABLE IF NOT EXISTS delivery_turno_entrega (
    id                  BIGSERIAL PRIMARY KEY,
    estabelecimento_id  UUID NOT NULL,
    iniciado_em         TIMESTAMPTZ NOT NULL,
    dia_local           DATE NOT NULL,
    numero_no_dia       INTEGER NOT NULL CHECK (numero_no_dia >= 1),
    motivo              TEXT NOT NULL CHECK (motivo IN ('primeiro_turno', 'fechada_2h', 'renovacao_24h')),
    total               INTEGER NOT NULL DEFAULT 0 CHECK (total >= 0),
    criado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_turno_entrega_inicio ON delivery_turno_entrega (estabelecimento_id, iniciado_em);
CREATE INDEX IF NOT EXISTS ix_delivery_turno_entrega_dia ON delivery_turno_entrega (estabelecimento_id, dia_local);

CREATE TABLE IF NOT EXISTS delivery_turno_estado (
    estabelecimento_id  UUID PRIMARY KEY,
    turno_id            BIGINT NULL REFERENCES delivery_turno_entrega (id) ON DELETE SET NULL,
    avaliado_em         TIMESTAMPTZ NOT NULL,
    aberta              BOOLEAN NOT NULL,
    fechada_desde       TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS delivery_turno_credito (
    id                  BIGSERIAL PRIMARY KEY,
    estabelecimento_id  UUID NOT NULL,
    turno_id            BIGINT NULL REFERENCES delivery_turno_entrega (id) ON DELETE CASCADE,
    motoboy_id          INTEGER NOT NULL,
    pedido_id           INTEGER NULL,
    gasto_em            TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_turno_credito_turno
    ON delivery_turno_credito (turno_id, motoboy_id) WHERE turno_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_turno_credito_pendente
    ON delivery_turno_credito (estabelecimento_id, motoboy_id) WHERE turno_id IS NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261212_01_turno_entrega')
ON CONFLICT (version) DO NOTHING;

COMMIT;
