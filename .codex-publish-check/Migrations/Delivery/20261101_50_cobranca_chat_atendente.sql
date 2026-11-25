BEGIN;

-- ---------------------------------------------------------------------------
-- Cobranca Pix feita pelo atendente no chat (sessao 5). Contrato: docs/pagamentos/CONTRATOS.md §2.16, §3.3 e §8.5.
--   cobranca_chat            ganha moeda, cliente, o link publico cifrado e o estado 'aguardando_aprovacao' (acima do limite)
--   cobranca_chat_config     a loja liga/desliga a cobranca pelo chat, define o limite sem aprovacao e o modelo da Meta
--   cobranca_chat_aprovacao  pedidos de aprovacao (emitir acima do limite, reembolsar, trocar vinculo) e a decisao do gerente
--   cobranca_chat_tarefa     o que acontece depois do pagamento fora da transacao (avisar o cliente, avancar o pedido,
--                            cancelar a cobranca reemitida quando a anterior foi paga atrasada). Uma linha por cobranca+tipo.
-- Requer 20261031_01 (nucleo Mercado Pago). Aditiva e idempotente; rollback em rollback/20261101_50_cobranca_chat_atendente.down.sql.
-- ---------------------------------------------------------------------------

DO $$
BEGIN
    IF to_regclass('cobranca_chat') IS NULL OR to_regclass('pagamento_intent') IS NULL THEN
        RAISE EXCEPTION 'cobranca_chat_atendente requer o nucleo Mercado Pago (20261031_01)';
    END IF;
END $$;

ALTER TABLE cobranca_chat
    ADD COLUMN IF NOT EXISTS moeda               TEXT NOT NULL DEFAULT 'BRL',
    ADD COLUMN IF NOT EXISTS cliente_id          UUID NULL,
    -- Token da pagina Pix publica cifrado (IPagamentoSegredoProtector, contexto cobranca_chat.link:{id}): o mesmo link vale
    -- em todo reenvio. Sem ele so existiria o hash na intencao e cada reenvio quebraria o link mandado antes.
    ADD COLUMN IF NOT EXISTS link_token_cifrado  TEXT NULL,
    ADD COLUMN IF NOT EXISTS ultimo_erro         TEXT NULL;

ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS ck_cobranca_chat_moeda;
ALTER TABLE cobranca_chat ADD CONSTRAINT ck_cobranca_chat_moeda CHECK (moeda = 'BRL');

-- O CHECK de status nasceu sem nome na 20261031_01 (cobranca_chat_status_check).
ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS cobranca_chat_status_check;
ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS ck_cobranca_chat_status;
ALTER TABLE cobranca_chat ADD CONSTRAINT ck_cobranca_chat_status CHECK (status IN (
    'aguardando_aprovacao', 'criando', 'aguardando', 'enviado', 'pago', 'expirado', 'cancelado',
    'reembolso_solicitado', 'reembolsado', 'recusado'));

-- Um pedido (ou pre-pedido) tem no maximo uma cobranca aberta na loja, em qualquer conversa.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobranca_chat_aberta_pedido_loja ON cobranca_chat (id_estabelecimento, pedido_id)
    WHERE pedido_id IS NOT NULL AND status IN ('aguardando_aprovacao', 'criando', 'aguardando', 'enviado');
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobranca_chat_aberta_pre_pedido_loja ON cobranca_chat (id_estabelecimento, pre_pedido_id)
    WHERE pre_pedido_id IS NOT NULL AND status IN ('aguardando_aprovacao', 'criando', 'aguardando', 'enviado');
-- Recuperacao: cobrancas presas em 'criando' (Pix sem resposta) e aprovacoes esquecidas.
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_pendentes ON cobranca_chat (created_at)
    WHERE status IN ('criando', 'aguardando_aprovacao');
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_reemitida ON cobranca_chat (reemitida_de) WHERE reemitida_de IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_pedido ON cobranca_chat (id_estabelecimento, pedido_id) WHERE pedido_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS cobranca_chat_config (
    id_estabelecimento          UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    habilitada                  BOOLEAN NOT NULL DEFAULT TRUE,
    permitir_avulsa             BOOLEAN NOT NULL DEFAULT TRUE,
    -- Acima deste valor o atendente sem WhatsApp:aprovar_cobranca precisa de aprovacao. NULL = sem limite.
    limite_sem_aprovacao        NUMERIC(12, 2) NULL CHECK (limite_sem_aprovacao IS NULL OR limite_sem_aprovacao >= 0),
    -- Modelo aprovado da Meta usado quando a janela de 24 h esta fechada. NULL = fora da janela nao envia.
    template_nome               TEXT NULL CHECK (template_nome IS NULL OR length(template_nome) BETWEEN 1 AND 512),
    template_idioma             TEXT NOT NULL DEFAULT 'pt_BR',
    confirmar_pagamento_cliente BOOLEAN NOT NULL DEFAULT TRUE,
    atualizado_por              INTEGER NULL,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS cobranca_chat_aprovacao (
    id                  UUID PRIMARY KEY,
    id_estabelecimento  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    id_cobranca         UUID NOT NULL REFERENCES cobranca_chat (id) ON DELETE CASCADE,
    acao                TEXT NOT NULL CHECK (acao IN ('emitir', 'reembolsar', 'trocar_vinculo')),
    status              TEXT NOT NULL DEFAULT 'pendente'
        CHECK (status IN ('pendente', 'aprovada', 'rejeitada', 'cancelada', 'falhou')),
    valor               NUMERIC(12, 2) NULL CHECK (valor IS NULL OR valor > 0),
    pedido_id           INTEGER NULL,
    pre_pedido_id       UUID NULL,
    motivo              TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 300),
    solicitado_por      INTEGER NOT NULL,
    decidido_por        INTEGER NULL,
    decidido_em         TIMESTAMPTZ NULL,
    motivo_decisao      TEXT NULL CHECK (motivo_decisao IS NULL OR length(motivo_decisao) <= 300),
    resultado           JSONB NULL,
    idempotency_key     TEXT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_cobranca_chat_aprovacao_idempotency UNIQUE (id_estabelecimento, idempotency_key)
);
-- Um pedido pendente por cobranca e acao (clique duplo nao abre dois).
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobranca_chat_aprovacao_pendente ON cobranca_chat_aprovacao (id_cobranca, acao)
    WHERE status = 'pendente';
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_aprovacao_loja ON cobranca_chat_aprovacao (id_estabelecimento, status, created_at DESC);

CREATE TABLE IF NOT EXISTS cobranca_chat_tarefa (
    id                  BIGSERIAL PRIMARY KEY,
    id_cobranca         UUID NOT NULL REFERENCES cobranca_chat (id) ON DELETE CASCADE,
    id_estabelecimento  UUID NOT NULL,
    tipo                TEXT NOT NULL CHECK (tipo IN ('confirmar_cliente', 'avancar_pedido', 'cancelar_sucessora')),
    status              TEXT NOT NULL DEFAULT 'pendente' CHECK (status IN ('pendente', 'feita', 'ignorada', 'falhou')),
    tentativas          INTEGER NOT NULL DEFAULT 0,
    proxima_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resultado           TEXT NULL,
    ultimo_erro         TEXT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    concluida_em        TIMESTAMPTZ NULL,
    -- Webhook duplicado ou reprocessado nunca cria a mesma tarefa duas vezes.
    CONSTRAINT ux_cobranca_chat_tarefa UNIQUE (id_cobranca, tipo)
);
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_tarefa_fila ON cobranca_chat_tarefa (proxima_em) WHERE status = 'pendente';

COMMIT;
