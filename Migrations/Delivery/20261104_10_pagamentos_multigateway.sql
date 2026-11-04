BEGIN;

-- Base aditiva para mais de um provedor por estabelecimento.
-- Mercado Pago continua sendo o padrao quando nao existe preferencia explicita.

CREATE TABLE IF NOT EXISTS pagamento_provedor_loja (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    provedor               TEXT NOT NULL CHECK (provedor IN ('mercadopago', 'infinitepay')),
    habilitado             BOOLEAN NOT NULL DEFAULT FALSE,
    status                 TEXT NOT NULL DEFAULT 'nao_configurado'
        CHECK (status IN ('nao_configurado', 'configurado', 'recebimento_validado', 'erro')),
    versao                 INTEGER NOT NULL DEFAULT 1 CHECK (versao > 0),
    ultimo_erro            TEXT NULL,
    validado_em            TIMESTAMPTZ NULL,
    atualizado_por         INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_pagamento_provedor_loja UNIQUE (id_estabelecimento, provedor)
);

CREATE TABLE IF NOT EXISTS pagamento_preferencia_provedor (
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    fluxo                  TEXT NOT NULL CHECK (fluxo IN ('checkout_online', 'pix_exclusivo')),
    habilitado             BOOLEAN NOT NULL DEFAULT TRUE,
    provedor               TEXT NULL CHECK (provedor IN ('mercadopago', 'infinitepay')),
    atualizado_por         INTEGER NULL,
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id_estabelecimento, fluxo),
    CONSTRAINT ck_pagamento_preferencia_provedor_escolha CHECK (
        (habilitado AND provedor IS NOT NULL) OR (NOT habilitado AND provedor IS NULL)
    )
);

CREATE TABLE IF NOT EXISTS infinitepay_conta_loja (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    infinite_tag_normalizada TEXT NOT NULL CHECK (
        length(infinite_tag_normalizada) BETWEEN 1 AND 80
        AND infinite_tag_normalizada = lower(trim(infinite_tag_normalizada))
        AND infinite_tag_normalizada !~ '[[:space:]$]'
    ),
    habilitada             BOOLEAN NOT NULL DEFAULT FALSE,
    status                 TEXT NOT NULL DEFAULT 'configurado'
        CHECK (status IN ('configurado', 'recebimento_validado', 'erro')),
    versao                 INTEGER NOT NULL DEFAULT 1 CHECK (versao > 0),
    ultimo_erro            TEXT NULL,
    recebimento_validado_em TIMESTAMPTZ NULL,
    atualizado_por         INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_infinitepay_conta_loja UNIQUE (id_estabelecimento),
    CONSTRAINT ux_infinitepay_conta_loja_id_estabelecimento UNIQUE (id, id_estabelecimento)
);

-- Cada alteracao de handle ou API Key cria uma versao. Intencoes futuras apontam
-- para a versao ativa; intencoes antigas continuam apontando para a versao original.
CREATE TABLE IF NOT EXISTS infinitepay_conta_versao (
    id                     UUID PRIMARY KEY,
    id_conta               UUID NOT NULL,
    id_estabelecimento     UUID NOT NULL,
    versao                 INTEGER NOT NULL CHECK (versao > 0),
    infinite_tag_snapshot  TEXT NOT NULL,
    api_key_cifrada        TEXT NULL,
    ativa                  BOOLEAN NOT NULL DEFAULT TRUE,
    criada_por             INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    rotacionada_em         TIMESTAMPTZ NULL,
    CONSTRAINT ux_infinitepay_conta_versao UNIQUE (id_conta, versao),
    CONSTRAINT ux_infinitepay_versao_loja UNIQUE (id_estabelecimento, id),
    CONSTRAINT fk_infinitepay_versao_conta_loja FOREIGN KEY (id_conta, id_estabelecimento)
        REFERENCES infinitepay_conta_loja (id, id_estabelecimento) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_infinitepay_conta_versao_ativa
    ON infinitepay_conta_versao (id_conta) WHERE ativa;

ALTER TABLE pagamento_intent ADD COLUMN IF NOT EXISTS provedor TEXT NOT NULL DEFAULT 'mercadopago';
ALTER TABLE pagamento_intent ADD COLUMN IF NOT EXISTS id_infinitepay_conta_versao UUID NULL;

ALTER TABLE pagamento_tentativa ADD COLUMN IF NOT EXISTS provedor TEXT NOT NULL DEFAULT 'mercadopago';
ALTER TABLE pagamento_tentativa ADD COLUMN IF NOT EXISTS referencia_provedor TEXT NULL;

ALTER TABLE pagamento ADD COLUMN IF NOT EXISTS provedor TEXT NOT NULL DEFAULT 'mercadopago';
ALTER TABLE pagamento ADD COLUMN IF NOT EXISTS referencia_provedor TEXT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pagamento_intent_provedor') THEN
        ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_provedor
            CHECK (provedor IN ('mercadopago', 'infinitepay'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pagamento_tentativa_provedor') THEN
        ALTER TABLE pagamento_tentativa ADD CONSTRAINT ck_pagamento_tentativa_provedor
            CHECK (provedor IN ('mercadopago', 'infinitepay'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pagamento_provedor') THEN
        ALTER TABLE pagamento ADD CONSTRAINT ck_pagamento_provedor
            CHECK (provedor IN ('mercadopago', 'infinitepay'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_pagamento_intent_infinitepay_loja') THEN
        ALTER TABLE pagamento_intent ADD CONSTRAINT fk_pagamento_intent_infinitepay_loja
            FOREIGN KEY (id_estabelecimento, id_infinitepay_conta_versao)
            REFERENCES infinitepay_conta_versao (id_estabelecimento, id) ON DELETE RESTRICT;
    END IF;
END $$;

-- Amplia a regra existente sem alterar nenhum vinculo Mercado Pago.
ALTER TABLE pagamento_intent DROP CONSTRAINT IF EXISTS ck_pagamento_intent_conta;
ALTER TABLE pagamento_intent ADD CONSTRAINT ck_pagamento_intent_conta CHECK (
    (provedor = 'mercadopago' AND origem IN ('cardapio', 'chat') AND conta_tipo = 'loja'
        AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'mercadopago' AND origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy'
        AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL AND id_infinitepay_conta_versao IS NULL)
    OR (provedor = 'infinitepay' AND origem = 'cardapio' AND conta_tipo = 'loja'
        AND id_conta_loja IS NULL AND id_conta_zippy IS NULL AND id_infinitepay_conta_versao IS NOT NULL)
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261104_10_pagamentos_multigateway')
ON CONFLICT (version) DO NOTHING;

COMMIT;
