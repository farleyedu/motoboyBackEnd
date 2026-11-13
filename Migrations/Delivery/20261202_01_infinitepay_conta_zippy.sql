BEGIN;

-- Conta InfinitePay da PROPRIA Zippy (recebedora de mensalidades e compras extras). Uma unica linha: InfinitePay nao tem sandbox.
-- A API Key (opcional) fica cifrada na versao; cada troca de InfiniteTag/chave cria versao nova e as cobrancas ja emitidas
-- continuam apontando para a versao com que nasceram.
CREATE TABLE IF NOT EXISTS infinitepay_conta_zippy (
    id                       UUID PRIMARY KEY,
    infinite_tag_normalizada TEXT NOT NULL CHECK (
        length(infinite_tag_normalizada) BETWEEN 1 AND 80
        AND infinite_tag_normalizada = lower(trim(infinite_tag_normalizada))
        AND infinite_tag_normalizada !~ '[[:space:]$]'
    ),
    habilitada               BOOLEAN NOT NULL DEFAULT FALSE,
    versao                   INTEGER NOT NULL DEFAULT 1 CHECK (versao > 0),
    atualizado_por           INTEGER NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_infinitepay_conta_zippy_unica ON infinitepay_conta_zippy ((TRUE));

CREATE TABLE IF NOT EXISTS infinitepay_conta_zippy_versao (
    id                     UUID PRIMARY KEY,
    id_conta               UUID NOT NULL REFERENCES infinitepay_conta_zippy (id) ON DELETE RESTRICT,
    versao                 INTEGER NOT NULL CHECK (versao > 0),
    infinite_tag_snapshot  TEXT NOT NULL,
    api_key_cifrada        TEXT NULL,
    ativa                  BOOLEAN NOT NULL DEFAULT TRUE,
    criada_por             INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    rotacionada_em         TIMESTAMPTZ NULL,
    CONSTRAINT ux_infinitepay_conta_zippy_versao UNIQUE (id_conta, versao)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_infinitepay_conta_zippy_versao_ativa ON infinitepay_conta_zippy_versao (id_conta) WHERE ativa;

-- A intencao e o checkout de assinatura/compra extra apontam para a versao da conta ZIPPY (nunca para conta de loja).
ALTER TABLE pagamento_intent ADD COLUMN IF NOT EXISTS id_infinitepay_zippy_versao UUID NULL REFERENCES infinitepay_conta_zippy_versao (id) ON DELETE RESTRICT;
ALTER TABLE infinitepay_checkout ADD COLUMN IF NOT EXISTS id_conta_zippy_versao UUID NULL REFERENCES infinitepay_conta_zippy_versao (id) ON DELETE RESTRICT;
-- A FK composta com a loja continua valendo para checkouts de loja (ignorada quando id_conta_versao e nulo).
ALTER TABLE infinitepay_checkout ALTER COLUMN id_conta_versao DROP NOT NULL;
ALTER TABLE infinitepay_checkout DROP CONSTRAINT IF EXISTS ck_infinitepay_checkout_conta;
ALTER TABLE infinitepay_checkout ADD CONSTRAINT ck_infinitepay_checkout_conta
    CHECK ((id_conta_versao IS NOT NULL)::int + (id_conta_zippy_versao IS NOT NULL)::int = 1);

ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (provedor = 'mercadopago' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL AND id_infinitepay_zippy_versao IS NULL)
    OR (provedor = 'mercadopago' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL AND id_infinitepay_conta_versao IS NULL AND id_infinitepay_zippy_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NOT NULL AND id_infinitepay_zippy_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL AND id_infinitepay_zippy_versao IS NOT NULL)
);

-- Cobranca comercial paga por link: novos valores de meio e pago_via (os existentes nao mudam).
DO $$
DECLARE r RECORD;
BEGIN
    FOR r IN SELECT conname FROM pg_constraint
              WHERE conrelid = 'comercial_cobranca'::regclass AND contype = 'c'
                AND (pg_get_constraintdef(oid) LIKE '%pago_via%' OR pg_get_constraintdef(oid) LIKE '%meio%')
    LOOP
        EXECUTE format('ALTER TABLE comercial_cobranca DROP CONSTRAINT %I', r.conname);
    END LOOP;
END $$;
ALTER TABLE comercial_cobranca ADD CONSTRAINT ck_comercial_cobranca_meio
    CHECK (meio IS NULL OR meio IN ('pix', 'cartao', 'cartao_recorrente', 'manual', 'link'));
ALTER TABLE comercial_cobranca ADD CONSTRAINT ck_comercial_cobranca_pago_via
    CHECK (pago_via IS NULL OR pago_via IN ('mercadopago', 'mp_recorrente', 'manual', 'infinitepay'));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261202_01_infinitepay_conta_zippy')
ON CONFLICT (version) DO NOTHING;

COMMIT;
