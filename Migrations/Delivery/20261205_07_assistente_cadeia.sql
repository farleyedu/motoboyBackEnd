BEGIN;

-- Assistente da Zippy: cadeia de IAs (CONTRATO-API §10). A configuração passa a ter provedores editáveis (Groq, Gemini, OpenRouter,
-- OpenAI, Anthropic...) e uma cadeia ordenada com troca automática. provedor/modelo continuam gravados (primeiro item da cadeia) para
-- leitura antiga; linha sem cadeia é lida como principal + reserva. Chaves seguem só em variável de ambiente.
-- assistente_cadeia_estado: "esgotado até" e "desligado por erro" de cada item, no banco para valer em mais de uma instância.
-- Requer 20261205_01, _03 e _05. Aditiva e idempotente.

ALTER TABLE assistente_config ADD COLUMN IF NOT EXISTS cadeia JSONB NULL;
ALTER TABLE assistente_config ADD COLUMN IF NOT EXISTS provedores JSONB NULL;
ALTER TABLE assistente_config ADD COLUMN IF NOT EXISTS modo TEXT NULL;
ALTER TABLE assistente_config ADD COLUMN IF NOT EXISTS pagos TEXT NULL;

-- O provedor agora é um id da lista editável (groq, gemini...), não só anthropic/openai.
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_provedor;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_provedor CHECK (provedor ~ '^[a-z0-9]+(-[a-z0-9]+)*$');
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_modo;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_modo CHECK (modo IS NULL OR modo IN ('ordem', 'revezar'));
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_pagos;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_pagos CHECK (pagos IS NULL OR pagos IN ('pagoSoNoFim', 'nuncaPago'));

CREATE TABLE IF NOT EXISTS assistente_cadeia_estado (
    item_id       TEXT PRIMARY KEY,
    -- Provedor + modelo do item quando o estado foi gravado: se a Gestão trocar o item, o estado antigo não vale.
    provedor      TEXT NOT NULL,
    modelo        TEXT NOT NULL,
    esgotado_ate  TIMESTAMPTZ NULL,
    desligado     BOOLEAN NOT NULL DEFAULT FALSE,
    erro          TEXT NULL,
    atualizado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Qual item da cadeia respondeu (conta o limite próprio do dia e o uso por item).
ALTER TABLE assistente_uso ADD COLUMN IF NOT EXISTS item_id TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_assistente_uso_item ON assistente_uso (item_id, criado_em) WHERE item_id IS NOT NULL;

-- Item pulado vira evento "pulo" (detalhe = motivo, extra = id do item).
ALTER TABLE assistente_eventos DROP CONSTRAINT IF EXISTS ck_assistente_eventos_tipo;
ALTER TABLE assistente_eventos ADD CONSTRAINT ck_assistente_eventos_tipo
    CHECK (tipo IN ('erro', 'limite', 'chamado_enviado', 'sem_funcionalidade', 'pulo'));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_07_assistente_cadeia')
ON CONFLICT (version) DO NOTHING;

COMMIT;
