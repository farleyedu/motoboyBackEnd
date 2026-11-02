-- Acompanhamento: fases e passagens (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin, itens 1 e 2).
--   * acompanhamento: uma linha por loja (ou oportunidade, antes de existir loja) com fase, data de entrada, faixa e versao.
--     Nasce na primeira consulta, a partir da coluna da oportunidade ou da situacao comercial da loja (fonte unica).
--   * acompanhamento_passagem: trilha de cada passagem com autor, motivo e chave de idempotencia (unica por loja).
--   * acompanhamento_historico: mudancas de fase e de faixa, com quem, quando e por que ("movimentos da semana").
--   * acompanhamento_proposta_versao: cada proposta guardada por versao (rascunho, enviada, aceita, guardada).
-- A passagem trava a linha do acompanhamento (FOR UPDATE) e grava tudo numa transacao; a chave unica segura o duplo clique.
-- Aditiva e idempotente (roda a cada boot). Sem as tabelas do CRM ou de lojas (bancos de teste), cria sem as chaves estrangeiras.
BEGIN;

CREATE TABLE IF NOT EXISTS acompanhamento (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    oportunidade_id       UUID NULL,
    estabelecimento_id    UUID NULL,
    fase                  TEXT NOT NULL CHECK (fase IN ('conversa', 'proposta', 'implantacao', 'teste', 'operando', 'saindo', 'perdido', 'pausa', 'encerrado')),
    fase_desde            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    faixa                 TEXT NOT NULL DEFAULT 'bem' CHECK (faixa IN ('bem', 'atencao', 'precisa_ajuda')),
    faixa_desde           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    versao                BIGINT NOT NULL DEFAULT 1,
    documento             VARCHAR(14) NULL CHECK (documento ~ '^[0-9]{11}$' OR documento ~ '^[0-9]{14}$'),
    quem_decide           TEXT NULL,
    pedidos_por_dia       INTEGER NULL CHECK (pedidos_por_dia > 0),
    dor_principal         TEXT NULL,
    valor_mensal          NUMERIC(12, 2) NULL,
    -- Passou pelo teste de 15 dias (ou ja operava antes do acompanhamento). Reimplantacao nao zera.
    teste_concluido_em    TIMESTAMPTZ NULL,
    prorrogacoes_no_teste INTEGER NOT NULL DEFAULT 0,
    -- "Teste sem compromisso": o teste comeca sozinho quando a implantacao ficar pronta.
    teste_quando_pronta   BOOLEAN NOT NULL DEFAULT FALSE,
    pausa_ate             DATE NULL,
    retomar_em            DATE NULL,
    saida_quando          TEXT NULL CHECK (saida_quando IN ('aviso_30_dias', 'fim_do_ciclo', 'agora')),
    saida_em              DATE NULL,
    forma_pagamento       TEXT NULL,
    cobranca_inicia_em    DATE NULL,
    criado_em             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_acompanhamento_dono CHECK (oportunidade_id IS NOT NULL OR estabelecimento_id IS NOT NULL)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_oportunidade ON acompanhamento (oportunidade_id) WHERE oportunidade_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_estabelecimento ON acompanhamento (estabelecimento_id) WHERE estabelecimento_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_acompanhamento_fase ON acompanhamento (fase, fase_desde);

CREATE TABLE IF NOT EXISTS acompanhamento_passagem (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    acompanhamento_id   UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    chave_idempotencia  VARCHAR(120) NOT NULL,
    de                  TEXT NOT NULL,
    para                TEXT NOT NULL,
    fase_resultante     TEXT NOT NULL,
    nome                TEXT NOT NULL,
    status              TEXT NOT NULL CHECK (status IN ('feita', 'pendente_aprovacao')),
    autor_usuario_id    INTEGER NULL,
    autor_tipo          TEXT NOT NULL CHECK (autor_tipo IN ('usuario', 'sistema')),
    motivo              TEXT NULL,
    dados               JSONB NOT NULL DEFAULT '{}'::jsonb,
    criada_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_passagem_chave UNIQUE (acompanhamento_id, chave_idempotencia)
);

CREATE INDEX IF NOT EXISTS ix_acompanhamento_passagem_loja ON acompanhamento_passagem (acompanhamento_id, criada_em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_historico (
    id                  BIGSERIAL PRIMARY KEY,
    acompanhamento_id   UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    tipo                TEXT NOT NULL CHECK (tipo IN ('fase', 'faixa')),
    de                  TEXT NULL,
    para                TEXT NOT NULL,
    em                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    autor_usuario_id    INTEGER NULL,
    autor_tipo          TEXT NOT NULL CHECK (autor_tipo IN ('usuario', 'sistema')),
    motivo              TEXT NULL,
    passagem_id         UUID NULL REFERENCES acompanhamento_passagem (id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_acompanhamento_historico_loja ON acompanhamento_historico (acompanhamento_id, em DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_historico_em ON acompanhamento_historico (em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_proposta_versao (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    oportunidade_id     UUID NOT NULL,
    versao              INTEGER NOT NULL CHECK (versao > 0),
    status              TEXT NOT NULL CHECK (status IN ('rascunho', 'enviada', 'aceita', 'guardada')),
    valor_mensal        NUMERIC(12, 2) NULL,
    valor_implantacao   NUMERIC(12, 2) NULL,
    dados               JSONB NOT NULL DEFAULT '{}'::jsonb,
    criada_por          INTEGER NULL,
    criada_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    enviada_em          TIMESTAMPTZ NULL,
    validade_ate        DATE NULL,
    passagem_id         UUID NULL REFERENCES acompanhamento_passagem (id) ON DELETE SET NULL,
    CONSTRAINT ux_acompanhamento_proposta_versao UNIQUE (oportunidade_id, versao)
);

DO $$
BEGIN
    IF to_regclass('estabelecimentos') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_acompanhamento_estabelecimento') THEN
        ALTER TABLE acompanhamento ADD CONSTRAINT fk_acompanhamento_estabelecimento
            FOREIGN KEY (estabelecimento_id) REFERENCES estabelecimentos (id) ON DELETE RESTRICT;
    END IF;
    IF to_regclass('crm_oportunidade') IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_acompanhamento_oportunidade') THEN
            ALTER TABLE acompanhamento ADD CONSTRAINT fk_acompanhamento_oportunidade
                FOREIGN KEY (oportunidade_id) REFERENCES crm_oportunidade (id) ON DELETE RESTRICT;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_acompanhamento_proposta_oportunidade') THEN
            ALTER TABLE acompanhamento_proposta_versao ADD CONSTRAINT fk_acompanhamento_proposta_oportunidade
                FOREIGN KEY (oportunidade_id) REFERENCES crm_oportunidade (id) ON DELETE CASCADE;
        END IF;
    END IF;
END $$;

COMMIT;
