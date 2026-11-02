-- Mensagens da Gestao para lojas e leads, por tres canais (docs/crm-inteligente/02-backend-whatsapp-ia.md, zippy-admin, Parte C).
--   gestao_canal / gestao_canal_remetente: os numeros Meta da Gestao (finalidade, dono, quem pode enviar por ele). Aponta para
--       canal_whatsapp: token, WABA e modelos continuam sendo os do canal (nenhum cliente Meta novo).
--   gestao_mensagem: historico unico da loja/lead. Saida (painel, numero, pessoal) e entrada (resposta real recebida no numero).
--       A janela de 24 h e por (canal, contato): vem da ultima 'entrada' daquele canal para aquele telefone.
--       estado: enviando -> enviado -> entregue -> lido | falhou (recibos da Meta); aguardando_confirmacao -> enviado_por_fora
--       (WhatsApp pessoal sem Meta: so a confirmacao humana conta, nunca finge entrega).
--   gestao_contato_optout: quem pediu para sair; bloqueia modelo de marketing.
-- Aditiva e idempotente (roda a cada boot). Nao envia nada e nao altera cobranca nem atendimento do restaurante.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_canal (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    canal_id        UUID NOT NULL UNIQUE REFERENCES canal_whatsapp (id),
    nome_exibicao   TEXT NOT NULL,
    finalidade      TEXT NOT NULL CHECK (finalidade IN ('comercial', 'suporte', 'financeiro', 'pessoal')),
    dono_usuario_id INTEGER NULL,
    ativo           BOOLEAN NOT NULL DEFAULT TRUE,
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_gestao_canal_dono CHECK ((finalidade = 'pessoal') = (dono_usuario_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_canal_dono ON gestao_canal (dono_usuario_id) WHERE dono_usuario_id IS NOT NULL AND ativo;

CREATE TABLE IF NOT EXISTS gestao_canal_remetente (
    gestao_canal_id UUID NOT NULL REFERENCES gestao_canal (id) ON DELETE CASCADE,
    usuario_id      INTEGER NOT NULL,
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (gestao_canal_id, usuario_id)
);

CREATE TABLE IF NOT EXISTS gestao_mensagem (
    id                  UUID PRIMARY KEY,
    direcao             TEXT NOT NULL CHECK (direcao IN ('saida', 'entrada')),
    via                 TEXT NOT NULL CHECK (via IN ('painel', 'numero', 'pessoal')),
    canal_id            UUID NULL REFERENCES canal_whatsapp (id),
    alvo_tipo           TEXT NULL CHECK (alvo_tipo IN ('loja', 'oportunidade')),
    estabelecimento_id  UUID NULL,
    oportunidade_id     UUID NULL,
    contato_ref         TEXT NULL,
    telefone_e164       VARCHAR(16) NULL,
    telefone_chave      VARCHAR(20) NULL,
    autor_usuario_id    INTEGER NULL,
    tipo_conteudo       TEXT NOT NULL DEFAULT 'texto' CHECK (tipo_conteudo IN ('texto', 'modelo')),
    texto               TEXT NULL,
    modelo_nome         TEXT NULL,
    modelo_idioma       TEXT NULL,
    modelo_categoria    TEXT NULL,
    variaveis           JSONB NULL,
    finalidade          TEXT NOT NULL DEFAULT 'utilidade' CHECK (finalidade IN ('utilidade', 'marketing')),
    estado              TEXT NOT NULL CHECK (estado IN ('enviando', 'enviado', 'entregue', 'lido', 'falhou', 'aguardando_confirmacao', 'enviado_por_fora', 'recebida')),
    erro_codigo         TEXT NULL,
    erro_detalhe        TEXT NULL,
    referencia_externa  TEXT NULL,
    chave_idempotencia  VARCHAR(120) NULL,
    chamado_id          BIGINT NULL,
    confirmado_por      INTEGER NULL,
    confirmado_em       TIMESTAMPTZ NULL,
    enviado_em          TIMESTAMPTZ NULL,
    entregue_em         TIMESTAMPTZ NULL,
    lido_em             TIMESTAMPTZ NULL,
    criado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_mensagem_idempotencia ON gestao_mensagem (autor_usuario_id, chave_idempotencia)
    WHERE chave_idempotencia IS NOT NULL AND direcao = 'saida';
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_mensagem_referencia ON gestao_mensagem (canal_id, direcao, referencia_externa)
    WHERE referencia_externa IS NOT NULL AND canal_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_mensagem_janela ON gestao_mensagem (canal_id, telefone_chave, criado_em DESC) WHERE direcao = 'entrada';
CREATE INDEX IF NOT EXISTS ix_gestao_mensagem_loja ON gestao_mensagem (estabelecimento_id, criado_em DESC) WHERE estabelecimento_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_mensagem_oportunidade ON gestao_mensagem (oportunidade_id, criado_em DESC) WHERE oportunidade_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_mensagem_recibo ON gestao_mensagem (referencia_externa) WHERE referencia_externa IS NOT NULL;

CREATE TABLE IF NOT EXISTS gestao_contato_optout (
    telefone_chave VARCHAR(20) PRIMARY KEY,
    desde          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    motivo         TEXT NULL,
    registrado_por INTEGER NULL
);

COMMIT;
