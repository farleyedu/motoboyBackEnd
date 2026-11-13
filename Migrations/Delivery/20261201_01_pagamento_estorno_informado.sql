BEGIN;

-- Estorno que a loja INFORMA ter feito no app/conta da InfinitePay. A InfinitePay nao tem estorno por API nem consulta de estorno, entao
-- esta linha nao prova que o dinheiro voltou: fica pendente ate a Zippy conferir uma evidencia. So a confirmacao mexe em pagamento_intent.
-- Tabela separada de pagamento_estorno, que exige uma linha em pagamento (a InfinitePay nao tem) e representa estorno feito por API.

CREATE TABLE IF NOT EXISTS pagamento_estorno_informado (
    id                  UUID PRIMARY KEY,
    id_estabelecimento  UUID NOT NULL,
    id_intent           UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    provedor            TEXT NOT NULL CHECK (provedor = 'infinitepay'),
    valor               NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    motivo              TEXT NOT NULL CHECK (length(motivo) BETWEEN 3 AND 300),
    referencia_externa  TEXT NULL CHECK (referencia_externa IS NULL OR length(referencia_externa) <= 120),
    status              TEXT NOT NULL DEFAULT 'pendente_confirmacao'
        CHECK (status IN ('pendente_confirmacao', 'confirmado', 'descartado')),
    idempotency_key     TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 160),
    informado_por       INTEGER NOT NULL,
    resolvido_por       INTEGER NULL,
    resolvido_em        TIMESTAMPTZ NULL,
    evidencia           TEXT NULL CHECK (evidencia IS NULL OR length(evidencia) <= 300),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_pagamento_estorno_informado_chave UNIQUE (id_estabelecimento, idempotency_key),
    -- Confirmar exige evidencia e outra pessoa: o clique de quem informou nunca vira "dinheiro devolvido".
    CONSTRAINT ck_pagamento_estorno_informado_confirmacao CHECK (
        status <> 'confirmado' OR (evidencia IS NOT NULL AND resolvido_por IS NOT NULL AND resolvido_por <> informado_por AND resolvido_em IS NOT NULL)
    )
);

-- No maximo um pendente por cobranca.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pagamento_estorno_informado_pendente
    ON pagamento_estorno_informado (id_intent) WHERE status = 'pendente_confirmacao';
CREATE INDEX IF NOT EXISTS ix_pagamento_estorno_informado_loja
    ON pagamento_estorno_informado (id_estabelecimento, created_at DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261201_01_pagamento_estorno_informado')
ON CONFLICT (version) DO NOTHING;

COMMIT;
