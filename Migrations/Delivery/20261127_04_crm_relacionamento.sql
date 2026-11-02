-- CRM, relacionamento: leads, responsavel real, proximo passo, marcacao manual, retencao e saida
-- (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin).
--   * crm_oportunidade: origem, decisor, whatsapp_e164, nome_normalizado (busca de parecidos sem acento), lead_chave/lead_hash (idempotencia).
--   * acompanhamento_relacionamento: responsavel atual, o anterior e ate quando ele ainda enxerga a loja (7 dias); versao para concorrencia.
--   * acompanhamento_proximo_passo: tipo + data; so um aberto por acompanhamento; os anteriores ficam (substituido/feito), nada e reescrito.
--   * acompanhamento_contato_marco: envio/recebimento EFETIVO de mensagem (referencia externa unica). Sem marco nao ha "aguardando resposta".
--   * acompanhamento_marcacao: marcacao manual com motivo; uma ativa por acompanhamento; remocao guarda quem e por que.
--   * acompanhamento_cobranca_responsavel, acompanhamento_rel_aviso: cobranca ao responsavel e avisos (transferencia, cobranca).
--   * acompanhamento_retencao: pedido de saida, leitura, oferta (desconto/estorno so aprovados), canal, resultado; chave de idempotencia.
--   * acompanhamento_relacionamento_evento: trilha unica e imutavel de tudo acima.
-- Aditiva e idempotente (roda a cada boot). Nao executa estorno, desconto nem envio de mensagem.
BEGIN;

DO $$
BEGIN
    IF to_regclass('crm_oportunidade') IS NOT NULL THEN
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS origem TEXT;
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS decisor TEXT;
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS whatsapp_e164 VARCHAR(16);
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS nome_normalizado TEXT;
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS lead_chave VARCHAR(120);
        ALTER TABLE crm_oportunidade ADD COLUMN IF NOT EXISTS lead_hash VARCHAR(64);
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_crm_oportunidade_whatsapp_e164') THEN
            ALTER TABLE crm_oportunidade ADD CONSTRAINT ck_crm_oportunidade_whatsapp_e164
                CHECK (whatsapp_e164 IS NULL OR whatsapp_e164 ~ '^\+[1-9][0-9]{7,14}$');
        END IF;

        UPDATE crm_oportunidade
           SET nome_normalizado = btrim(regexp_replace(
                   translate(lower(nome_empresa), 'áàâãäéèêëíìîïóòôõöúùûüçñ', 'aaaaaeeeeiiiiooooouuuucn'), '[^a-z0-9]+', ' ', 'g'))
         WHERE nome_normalizado IS NULL;

        CREATE INDEX IF NOT EXISTS ix_crm_oportunidade_whatsapp ON crm_oportunidade (whatsapp_e164) WHERE whatsapp_e164 IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_crm_oportunidade_nome_normalizado ON crm_oportunidade (nome_normalizado);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_oportunidade_lead_chave ON crm_oportunidade (lead_chave) WHERE lead_chave IS NOT NULL;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS acompanhamento_relacionamento (
    acompanhamento_id      UUID PRIMARY KEY REFERENCES acompanhamento (id) ON DELETE CASCADE,
    responsavel_usuario_id INTEGER NULL,
    responsavel_desde      TIMESTAMPTZ NULL,
    anterior_usuario_id    INTEGER NULL,
    visivel_anterior_ate   TIMESTAMPTZ NULL,
    versao                 BIGINT NOT NULL DEFAULT 1,
    criado_em              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_relacionamento_resp ON acompanhamento_relacionamento (responsavel_usuario_id);

CREATE TABLE IF NOT EXISTS acompanhamento_proximo_passo (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    acompanhamento_id UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    tipo              TEXT NOT NULL CHECK (tipo IN ('mensagem', 'ligacao', 'reuniao', 'proposta', 'visita', 'retorno')),
    data              DATE NOT NULL,
    nota              TEXT NULL,
    estado            TEXT NOT NULL DEFAULT 'aberto' CHECK (estado IN ('aberto', 'substituido', 'feito')),
    criado_por        INTEGER NULL,
    criado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    encerrado_em      TIMESTAMPTZ NULL,
    encerrado_por     INTEGER NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_proximo_passo_aberto ON acompanhamento_proximo_passo (acompanhamento_id) WHERE estado = 'aberto';
CREATE INDEX IF NOT EXISTS ix_acompanhamento_proximo_passo_data ON acompanhamento_proximo_passo (data) WHERE estado = 'aberto';

CREATE TABLE IF NOT EXISTS acompanhamento_contato_marco (
    id                BIGSERIAL PRIMARY KEY,
    acompanhamento_id UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    direcao           TEXT NOT NULL CHECK (direcao IN ('enviada', 'recebida')),
    em                TIMESTAMPTZ NOT NULL,
    referencia        TEXT NOT NULL,
    criado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_contato_marco UNIQUE (acompanhamento_id, direcao, referencia)
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_contato_marco_em ON acompanhamento_contato_marco (acompanhamento_id, direcao, em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_marcacao (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    acompanhamento_id UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    tipo              TEXT NOT NULL CHECK (tipo IN ('atencao', 'risco_de_saida', 'nao_incomodar')),
    motivo            TEXT NOT NULL,
    por_usuario_id    INTEGER NOT NULL,
    desde             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    removida_em       TIMESTAMPTZ NULL,
    removida_por      INTEGER NULL,
    removida_motivo   TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_marcacao_ativa ON acompanhamento_marcacao (acompanhamento_id) WHERE removida_em IS NULL;

CREATE TABLE IF NOT EXISTS acompanhamento_cobranca_responsavel (
    id                     BIGSERIAL PRIMARY KEY,
    acompanhamento_id      UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    cobrador_usuario_id    INTEGER NOT NULL,
    responsavel_usuario_id INTEGER NOT NULL,
    mensagem               TEXT NULL,
    em                     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_cobranca_responsavel ON acompanhamento_cobranca_responsavel (acompanhamento_id, cobrador_usuario_id, em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_rel_aviso (
    id                      BIGSERIAL PRIMARY KEY,
    destinatario_usuario_id INTEGER NOT NULL,
    acompanhamento_id       UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    tipo                    TEXT NOT NULL,
    mensagem                TEXT NOT NULL,
    de_usuario_id           INTEGER NULL,
    criado_em               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lido_em                 TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_rel_aviso_dest ON acompanhamento_rel_aviso (destinatario_usuario_id, criado_em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_retencao (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    acompanhamento_id   UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    chave_idempotencia  VARCHAR(120) NOT NULL,
    pedido              TEXT NOT NULL,
    pedido_em           TIMESTAMPTZ NOT NULL,
    leitura             TEXT NOT NULL,
    oferta_tipo         TEXT NOT NULL CHECK (oferta_tipo IN ('nenhuma', 'pausa', 'ajuda_operacional', 'desconto', 'estorno', 'outra')),
    oferta_descricao    TEXT NULL,
    oferta_valor        NUMERIC(12, 2) NULL,
    oferta_aprovada     BOOLEAN NOT NULL DEFAULT FALSE,
    oferta_aprovada_por INTEGER NULL,
    canal               TEXT NOT NULL CHECK (canal IN ('whatsapp', 'ligacao', 'presencial', 'email')),
    resultado           TEXT NOT NULL CHECK (resultado IN ('retida', 'saiu', 'em_negociacao', 'pausada')),
    saida_quando        TEXT NULL CHECK (saida_quando IN ('aviso_30_dias', 'fim_do_ciclo', 'agora')),
    saida_em            DATE NULL,
    pausa_ate           DATE NULL,
    cobranca_em_aberto  BOOLEAN NOT NULL DEFAULT FALSE,
    observacao          TEXT NULL,
    criado_por          INTEGER NULL,
    criado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_retencao_chave UNIQUE (acompanhamento_id, chave_idempotencia)
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_retencao_loja ON acompanhamento_retencao (acompanhamento_id, criado_em DESC);

CREATE TABLE IF NOT EXISTS acompanhamento_relacionamento_evento (
    id                BIGSERIAL PRIMARY KEY,
    acompanhamento_id UUID NOT NULL REFERENCES acompanhamento (id) ON DELETE CASCADE,
    tipo              TEXT NOT NULL,
    detalhe           JSONB NOT NULL DEFAULT '{}'::jsonb,
    usuario_id        INTEGER NULL,
    em                TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_acompanhamento_relacionamento_evento ON acompanhamento_relacionamento_evento (acompanhamento_id, em DESC, id DESC);

-- Quem ja tem responsavel no CRM (oportunidade ou contrato mais recente da loja) continua com ele.
DO $$
BEGIN
    IF to_regclass('crm_oportunidade') IS NOT NULL AND to_regclass('crm_contrato') IS NOT NULL THEN
        INSERT INTO acompanhamento_relacionamento (acompanhamento_id, responsavel_usuario_id, responsavel_desde)
        SELECT a.id,
               COALESCE(o.responsavel_usuario_id,
                        (SELECT c.responsavel_usuario_id FROM crm_contrato c
                          WHERE c.estabelecimento_id = a.estabelecimento_id AND c.responsavel_usuario_id IS NOT NULL
                          ORDER BY c.created_at DESC LIMIT 1)),
               NOW()
          FROM acompanhamento a
          LEFT JOIN crm_oportunidade o ON o.id = a.oportunidade_id
        ON CONFLICT (acompanhamento_id) DO NOTHING;
    END IF;
END $$;

COMMIT;
