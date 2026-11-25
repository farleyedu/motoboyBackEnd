BEGIN;

-- Webhook InfinitePay (sem assinatura): inbox persistente + pendencias de conciliacao + dados da confirmacao verificada no servidor.
-- Nada aqui prova pagamento: o aviso so vira pago depois de POST /payment_check conferido (ver docs/pagamentos/CONTRATOS.md, secao 13).

CREATE TABLE IF NOT EXISTS infinitepay_webhook_evento (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL,
    id_intent              UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    order_nsu              TEXT NOT NULL,
    tipo_evento            TEXT NULL CHECK (tipo_evento IS NULL OR tipo_evento IN ('created', 'paid', 'settled', 'canceled', 'refunded', 'payment_failed')),
    transaction_nsu        TEXT NULL,
    invoice_slug           TEXT NULL,
    amount_informado       BIGINT NULL,
    paid_amount_informado  BIGINT NULL,
    capture_method         TEXT NULL CHECK (capture_method IS NULL OR capture_method IN ('pix', 'credit_card')),
    -- Hash do conteudo relevante: o mesmo aviso reenviado nao cria segunda linha.
    dedupe_hash            TEXT NOT NULL,
    payload_json           TEXT NOT NULL CHECK (length(payload_json) <= 16384),
    status                 TEXT NOT NULL DEFAULT 'pendente'
        CHECK (status IN ('pendente', 'processando', 'processado', 'descartado', 'pendencia')),
    resultado              TEXT NULL,
    tentativas             INTEGER NOT NULL DEFAULT 0,
    proxima_tentativa_em   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    bloqueado_ate          TIMESTAMPTZ NULL,
    ultimo_erro            TEXT NULL,
    processado_em          TIMESTAMPTZ NULL,
    recebido_em            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_infinitepay_webhook_dedupe UNIQUE (dedupe_hash),
    CONSTRAINT fk_infinitepay_webhook_checkout FOREIGN KEY (order_nsu) REFERENCES infinitepay_checkout (order_nsu) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_infinitepay_webhook_fila ON infinitepay_webhook_evento (proxima_tentativa_em) WHERE status IN ('pendente', 'processando');
CREATE INDEX IF NOT EXISTS ix_infinitepay_webhook_intent ON infinitepay_webhook_evento (id_intent, recebido_em DESC);

CREATE TABLE IF NOT EXISTS infinitepay_pendencia (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL,
    id_intent              UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    id_evento              UUID NULL REFERENCES infinitepay_webhook_evento (id) ON DELETE RESTRICT,
    tipo                   TEXT NOT NULL CHECK (tipo IN (
        'cancelamento_nao_verificavel', 'estorno_nao_verificavel', 'pago_sem_identificadores', 'valor_divergente',
        'pagamento_duplicado', 'pago_em_estado_final', 'verificacao_falhou', 'nao_confirmado')),
    detalhe                JSONB NULL,
    status                 TEXT NOT NULL DEFAULT 'aberta' CHECK (status IN ('aberta', 'resolvida')),
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolvida_em           TIMESTAMPTZ NULL,
    resolvida_por          INTEGER NULL,
    CONSTRAINT ux_infinitepay_pendencia_evento UNIQUE (id_evento, tipo)
);
CREATE INDEX IF NOT EXISTS ix_infinitepay_pendencia_abertas ON infinitepay_pendencia (id_estabelecimento, created_at DESC) WHERE status = 'aberta';

-- Dados verificados no servidor (nunca os do aviso). transaction_nsu unico: uma transacao confirma uma unica cobranca.
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS confirmado_em TIMESTAMPTZ NULL;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS transaction_nsu TEXT NULL;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS invoice_slug TEXT NULL;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS capture_method TEXT NULL;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS amount_confirmado_centavos BIGINT NULL;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS paid_amount_centavos BIGINT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_infinitepay_checkout_transaction ON infinitepay_checkout (transaction_nsu) WHERE transaction_nsu IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261129_03_infinitepay_webhook')
ON CONFLICT (version) DO NOTHING;

COMMIT;
