BEGIN;

-- Assistente da Zippy: qual IA responde (CONTRATO-API §7), escolhida na Gestão da plataforma. Uma linha só (id = 1).
-- Sem linha = padrão do código (anthropic, claude-opus-5-5, esforço low). Chaves NÃO ficam aqui: só em variável de ambiente.
-- Também grava em cada resposta qual provedor respondeu e se foi a reserva (custo na Gestão).
-- Requer 20261205_01_assistente. Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS assistente_config (
    id               SMALLINT PRIMARY KEY,
    ligado           BOOLEAN NOT NULL DEFAULT TRUE,
    provedor         TEXT NOT NULL,
    modelo           TEXT NOT NULL,
    esforco          TEXT NOT NULL,
    max_tokens       INTEGER NOT NULL,
    limite_diario    INTEGER NOT NULL,
    reserva_provedor TEXT NULL,
    reserva_modelo   TEXT NULL,
    atualizado_por   INTEGER NULL REFERENCES usuario (id),
    atualizado_em    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_linha;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_linha CHECK (id = 1);
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_provedor;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_provedor CHECK (
    provedor IN ('anthropic', 'openai') AND (reserva_provedor IS NULL OR reserva_provedor IN ('anthropic', 'openai')));
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_reserva;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_reserva CHECK ((reserva_provedor IS NULL) = (reserva_modelo IS NULL));
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_esforco;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_esforco CHECK (esforco IN ('nenhum', 'low', 'medium', 'high'));
ALTER TABLE assistente_config DROP CONSTRAINT IF EXISTS ck_assistente_config_numeros;
ALTER TABLE assistente_config ADD CONSTRAINT ck_assistente_config_numeros
    CHECK (max_tokens BETWEEN 256 AND 32000 AND limite_diario BETWEEN 1 AND 10000);

-- Quem mudou e quando, com a configuração inteira daquele momento.
CREATE TABLE IF NOT EXISTS assistente_config_historico (
    id           BIGSERIAL PRIMARY KEY,
    config       JSONB NOT NULL,
    alterado_por INTEGER NULL REFERENCES usuario (id),
    alterado_em  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_assistente_config_historico_em ON assistente_config_historico (alterado_em DESC);

ALTER TABLE assistente_mensagens ADD COLUMN IF NOT EXISTS provedor TEXT NULL;
ALTER TABLE assistente_mensagens ADD COLUMN IF NOT EXISTS usou_reserva BOOLEAN NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_03_assistente_config')
ON CONFLICT (version) DO NOTHING;

COMMIT;
