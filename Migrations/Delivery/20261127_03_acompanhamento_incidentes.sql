-- Acompanhamento: falhas, incidentes compartilhados e avisos (docs/crm-inteligente/01-backend-base-crm.md).
--   * acompanhamento_falha: uma falha por loja (categoria, fonte, inicio/fim, causa provavel, executor, prova, estado). Nunca e apagada nem
--     reescrita: normalizar so preenche fim/estado; a causa original fica.
--   * acompanhamento_incidente: agregado de 3+ lojas com a mesma assinatura de falha aberta (um aberto por assinatura).
--   * acompanhamento_incidente_evento: linha do tempo consultavel (Quadro, ficha, testagem, cancelamento).
--   * acompanhamento_aviso / _lido: avisos navegaveis e leitura por usuario.
--   * acompanhamento_incidente_envio: mensagem preparada e confirmada por humano; uma por incidente/contato (idempotencia).
-- Aditiva e idempotente (roda a cada boot). Nao toca em pagamento nem no pipeline de mensagens.
BEGIN;

CREATE TABLE IF NOT EXISTS acompanhamento_incidente (
    id                UUID PRIMARY KEY,
    categoria         TEXT NOT NULL,
    assinatura        TEXT NOT NULL,
    estado            TEXT NOT NULL DEFAULT 'aberto' CHECK (estado IN ('aberto', 'normalizado')),
    inicio            TIMESTAMPTZ NOT NULL,
    fim               TIMESTAMPTZ NULL,
    causa_provavel    TEXT NOT NULL,
    executor          TEXT NOT NULL,
    lojas_afetadas    INTEGER NOT NULL DEFAULT 0,
    normalizou_por    INTEGER NULL,
    normalizou_nota   TEXT NULL,
    criado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_incidente_aberto ON acompanhamento_incidente (assinatura) WHERE estado = 'aberto';

CREATE TABLE IF NOT EXISTS acompanhamento_falha (
    id             UUID PRIMARY KEY,
    categoria      TEXT NOT NULL,
    fonte          TEXT NOT NULL,
    assinatura     TEXT NOT NULL,
    loja_id        UUID NOT NULL,
    inicio         TIMESTAMPTZ NOT NULL,
    fim            TIMESTAMPTZ NULL,
    causa_provavel TEXT NOT NULL,
    executor       TEXT NOT NULL CHECK (executor IN ('zippy', 'loja', 'terceiro', 'indeterminado')),
    prova          JSONB NOT NULL DEFAULT '{}'::jsonb,
    estado         TEXT NOT NULL DEFAULT 'aberta' CHECK (estado IN ('aberta', 'normalizada')),
    origem         TEXT NOT NULL CHECK (origem IN ('automatica', 'manual')),
    chave          TEXT NOT NULL,
    incidente_id   UUID NULL REFERENCES acompanhamento_incidente (id),
    criado_por     INTEGER NULL,
    criado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_falha_chave UNIQUE (chave)
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_falha_loja ON acompanhamento_falha (loja_id, inicio DESC);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_falha_aberta ON acompanhamento_falha (assinatura) WHERE estado = 'aberta';

CREATE TABLE IF NOT EXISTS acompanhamento_incidente_evento (
    id           BIGSERIAL PRIMARY KEY,
    incidente_id UUID NULL REFERENCES acompanhamento_incidente (id),
    falha_id     UUID NULL REFERENCES acompanhamento_falha (id),
    loja_id      UUID NULL,
    tipo         TEXT NOT NULL,
    detalhe      JSONB NOT NULL DEFAULT '{}'::jsonb,
    usuario_id   INTEGER NULL,
    em           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_incidente_evento_loja ON acompanhamento_incidente_evento (loja_id, em DESC);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_incidente_evento_inc ON acompanhamento_incidente_evento (incidente_id, em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_aviso (
    id           BIGSERIAL PRIMARY KEY,
    incidente_id UUID NOT NULL REFERENCES acompanhamento_incidente (id),
    tipo         TEXT NOT NULL CHECK (tipo IN ('incidente_aberto', 'incidente_normalizado')),
    texto        TEXT NOT NULL,
    criado_em    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_aviso UNIQUE (incidente_id, tipo)
);

CREATE TABLE IF NOT EXISTS acompanhamento_aviso_lido (
    aviso_id   BIGINT NOT NULL REFERENCES acompanhamento_aviso (id) ON DELETE CASCADE,
    usuario_id INTEGER NOT NULL,
    lido_em    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (aviso_id, usuario_id)
);

CREATE TABLE IF NOT EXISTS acompanhamento_incidente_envio (
    id            BIGSERIAL PRIMARY KEY,
    incidente_id  UUID NOT NULL REFERENCES acompanhamento_incidente (id),
    loja_id       UUID NOT NULL,
    contato       TEXT NOT NULL,
    mensagem      TEXT NOT NULL,
    estado        TEXT NOT NULL CHECK (estado IN ('pendente_servico', 'enviado', 'falhou')),
    preparado_por INTEGER NOT NULL,
    preparado_em  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_incidente_envio UNIQUE (incidente_id, contato)
);

COMMIT;
