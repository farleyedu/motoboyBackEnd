BEGIN;

-- Checkout hospedado da InfinitePay: uma linha por intencao. Guarda o corpo EXATO enviado a POST /links (para repetir sem
-- duplicar apos falha de rede) e a URL devolvida (nao existe endpoint para consultar link por order_nsu).
-- Nao guarda API Key: ela continua cifrada em infinitepay_conta_versao.
CREATE TABLE IF NOT EXISTS infinitepay_checkout (
    id                     UUID PRIMARY KEY,
    id_intent              UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    id_estabelecimento     UUID NOT NULL,
    id_conta_versao        UUID NOT NULL,
    -- order_nsu = external_reference da intencao (sem dado do cliente). A etapa de webhook acha a intencao por ele.
    order_nsu              TEXT NOT NULL,
    handle_snapshot        TEXT NOT NULL,
    valor_centavos         BIGINT NOT NULL CHECK (valor_centavos >= 100),
    usa_api_key            BOOLEAN NOT NULL,
    payload_json           TEXT NOT NULL,
    payload_hash           TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'criando' CHECK (status IN ('criando', 'criado', 'falhou')),
    tentativas             INTEGER NOT NULL DEFAULT 0,
    claim_ate              TIMESTAMPTZ NULL,
    checkout_url           TEXT NULL,
    resposta_hash          TEXT NULL,
    erro_codigo            TEXT NULL,
    erro_mensagem          TEXT NULL,
    expira_em              TIMESTAMPTZ NULL,
    criado_em              TIMESTAMPTZ NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_infinitepay_checkout_intent UNIQUE (id_intent),
    CONSTRAINT ux_infinitepay_checkout_order_nsu UNIQUE (order_nsu),
    CONSTRAINT fk_infinitepay_checkout_versao_loja FOREIGN KEY (id_estabelecimento, id_conta_versao)
        REFERENCES infinitepay_conta_versao (id_estabelecimento, id) ON DELETE RESTRICT,
    CONSTRAINT ck_infinitepay_checkout_url CHECK (status <> 'criado' OR (checkout_url IS NOT NULL AND checkout_url LIKE 'https://%'))
);
CREATE INDEX IF NOT EXISTS ix_infinitepay_checkout_loja ON infinitepay_checkout (id_estabelecimento, created_at DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261129_01_infinitepay_checkout')
ON CONFLICT (version) DO NOTHING;

COMMIT;
