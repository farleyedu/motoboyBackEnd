BEGIN;

-- ---------------------------------------------------------------------------
-- Pagamento online (Pix e cartao pelo Mercado Pago) no pre-pedido do cardapio web.
-- Pagamento e aceite sao coisas separadas:
--   aguardando_pagamento -> o numero ja foi confirmado e falta o dinheiro (so pedido online)
--   aguardando_aceite    -> pedido presencial confirmado OU pedido online confirmado E pago
--   aceitando            -> o atendente clicou em aceitar e o pedido real esta sendo criado (reserva contra a
--                           disputa com o cancelamento; volta a aguardando_aceite se a criacao falhar)
--   cancelado            -> o cliente desistiu, a confirmacao foi abandonada ou a loja cancelou depois do aceite
-- cardapio_pedido_reembolso guarda cada devolucao do cardapio (automatica ou pedida pela loja): a linha nasce na
-- mesma transacao que decide o estorno e o trabalhador do cardapio chama o Mercado Pago depois do commit.
-- Requer 20261002_03 (confirmacao pelo WhatsApp) e 20261031_01 (nucleo Mercado Pago). Aditiva e idempotente.
-- ---------------------------------------------------------------------------

DO $$
BEGIN
    IF to_regclass('cardapio_pedido_publico') IS NULL OR to_regclass('pagamento_intent') IS NULL
       OR to_regclass('pagamento_estorno') IS NULL THEN
        RAISE EXCEPTION 'cardapio_pagamento_online requer cardapio_pedido_publico e o nucleo Mercado Pago (20261031_01)';
    END IF;
END $$;

ALTER TABLE cardapio_pedido_publico
    ADD COLUMN IF NOT EXISTS pagamento_online     BOOLEAN     NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS metodo_online        TEXT        NULL,
    ADD COLUMN IF NOT EXISTS chave_idempotencia   TEXT        NULL,
    ADD COLUMN IF NOT EXISTS id_intent_atual      UUID        NULL REFERENCES pagamento_intent (id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS id_intent_pago       UUID        NULL REFERENCES pagamento_intent (id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS pago_em              TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS cancelado_em         TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS motivo_cancelamento  TEXT        NULL;

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_status;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_status CHECK (status IN (
        'pendente', 'aguardando_codigo', 'aguardando_pagamento', 'aguardando_aceite', 'aceitando', 'aceito',
        'recusado', 'expirado', 'cancelado'));

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_metodo_online;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_metodo_online CHECK (
        (pagamento_online AND metodo_online IN ('pix', 'cartao')) OR (NOT pagamento_online AND metodo_online IS NULL));

-- Retentativa do mesmo checkout (rede caiu, clique duplo) devolve o mesmo pre-pedido.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_pedido_publico_chave
    ON cardapio_pedido_publico (id_estabelecimento, chave_idempotencia)
    WHERE chave_idempotencia IS NOT NULL;

-- A fila do atendente passa a ver tambem o aceite em andamento.
DROP INDEX IF EXISTS ix_cardapio_pedido_publico_aguardando;
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_publico_aguardando
    ON cardapio_pedido_publico (id_estabelecimento, confirmado_em)
    WHERE status IN ('aguardando_aceite', 'aceitando');

-- Pago e sem confirmacao do numero: o trabalhador procura estes para devolver o dinheiro.
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_publico_pago_sem_confirmar
    ON cardapio_pedido_publico (pago_em)
    WHERE pagamento_online AND id_intent_pago IS NOT NULL AND status IN ('aguardando_codigo', 'expirado', 'pendente');

CREATE TABLE IF NOT EXISTS cardapio_pedido_reembolso (
    id                   UUID PRIMARY KEY,
    id_estabelecimento   UUID          NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    pre_pedido_id        UUID          NOT NULL REFERENCES cardapio_pedido_publico (id) ON DELETE CASCADE,
    id_intent            UUID          NOT NULL REFERENCES pagamento_intent (id),
    -- automatico: recusa, cancelamento antes do aceite, pagamento depois de cancelar, pagamento em dobro, confirmacao abandonada
    -- aprovacao_gerente: aceito e antes do preparo (integral)
    -- analise: em preparo ou entrega (total ou parcial, com justificativa)
    tipo                 TEXT          NOT NULL CHECK (tipo IN ('automatico', 'aprovacao_gerente', 'analise')),
    motivo               TEXT          NOT NULL CHECK (motivo IN ('recusa_loja', 'cancelamento_cliente', 'pago_apos_encerramento',
                                                                  'pagamento_duplicado', 'confirmacao_expirada', 'cancelamento_loja')),
    valor                NUMERIC(12,2) NOT NULL CHECK (valor > 0),
    justificativa        TEXT          NULL,
    status               TEXT          NOT NULL CHECK (status IN ('aguardando_aprovacao', 'reembolso_pendente', 'reembolso_em_processamento',
                                                                  'reembolsado', 'negado', 'falhou')),
    solicitado_por       INTEGER       NULL,
    decidido_por         INTEGER       NULL,
    decidido_em          TIMESTAMPTZ   NULL,
    id_estorno           UUID          NULL REFERENCES pagamento_estorno (id),
    idempotency_key      TEXT          NOT NULL,
    tentativas           INTEGER       NOT NULL DEFAULT 0,
    proxima_tentativa_em TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    ultimo_erro          TEXT          NULL,
    concluido_em         TIMESTAMPTZ   NULL,
    created_at           TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    updated_at           TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_cardapio_pedido_reembolso_justificativa CHECK (tipo <> 'analise' OR length(trim(coalesce(justificativa, ''))) >= 5)
);

-- Mesma decisao repetida (recusa clicada duas vezes, webhook reenviado) nao cria segundo reembolso.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_pedido_reembolso_chave
    ON cardapio_pedido_reembolso (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_reembolso_pre_pedido
    ON cardapio_pedido_reembolso (pre_pedido_id, created_at);
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_reembolso_fila
    ON cardapio_pedido_reembolso (proxima_tentativa_em)
    WHERE status = 'reembolso_pendente';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261101_30_cardapio_pagamento_online')
ON CONFLICT (version) DO NOTHING;

COMMIT;
