BEGIN;

-- Assistente da Zippy: parâmetros que cada provedor de IA mostrou não aceitar (CONTRATO-API §10.1, "aprende sozinho").
-- Quando um provedor compatível com OpenAI devolve 400 por strict, reasoning_effort ou esquema, o backend tenta uma vez sem o
-- parâmetro e grava aqui; as próximas chamadas já vão sem. Salvar o provedor com outra URL ou flags na Gestão apaga o dele.
-- Requer 20261205_07_assistente_cadeia. Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS assistente_provedor_nao_suporta (
    provedor  TEXT NOT NULL,
    parametro TEXT NOT NULL,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (provedor, parametro)
);
ALTER TABLE assistente_provedor_nao_suporta DROP CONSTRAINT IF EXISTS ck_assistente_provedor_nao_suporta_parametro;
ALTER TABLE assistente_provedor_nao_suporta ADD CONSTRAINT ck_assistente_provedor_nao_suporta_parametro
    CHECK (parametro IN ('strict', 'reasoning_effort', 'esquema'));

-- Alerta "perguntas sem IA hoje": evento erro com extra = 'cadeia_esgotada'.
CREATE INDEX IF NOT EXISTS ix_assistente_eventos_cadeia_esgotada ON assistente_eventos (criado_em) WHERE tipo = 'erro' AND extra = 'cadeia_esgotada';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_09_assistente_nao_suporta')
ON CONFLICT (version) DO NOTHING;

COMMIT;
