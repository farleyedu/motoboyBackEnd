BEGIN;

-- Assistente da Zippy: uso e custo na Gestão (CONTRATO-API §8).
-- assistente_uso: uma linha por resposta da IA (tokens, provedor, modelo, cartões). NÃO é apagada com a conversa (30 dias),
-- por isso não tem chave estrangeira para assistente_conversas.
-- assistente_eventos: o que não vira resposta: erro do provedor, bloqueio pelo limite diário, chamado enviado pelo chat,
-- pergunta sem funcionalidade no catálogo (com o texto da pergunta, para virar ideia).
-- assistente_config.precos: US$ por milhão de tokens por modelo; nulo = tabela padrão do código.
-- Requer 20261205_01_assistente e 20261205_03_assistente_config. Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS assistente_uso (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    conversa_id        UUID NOT NULL,
    usuario_id         INTEGER NOT NULL,
    provedor           TEXT NOT NULL,
    modelo             TEXT NOT NULL,
    usou_reserva       BOOLEAN NOT NULL DEFAULT FALSE,
    tokens_entrada     BIGINT NOT NULL DEFAULT 0,
    tokens_saida       BIGINT NOT NULL DEFAULT 0,
    tokens_cache_lidos BIGINT NOT NULL DEFAULT 0,
    acoes_propostas    INTEGER NOT NULL DEFAULT 0,
    telas              INTEGER NOT NULL DEFAULT 0,
    rascunhos          INTEGER NOT NULL DEFAULT 0,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_assistente_uso_em ON assistente_uso (criado_em);
CREATE INDEX IF NOT EXISTS ix_assistente_uso_loja ON assistente_uso (estabelecimento_id, criado_em);

CREATE TABLE IF NOT EXISTS assistente_eventos (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    conversa_id        UUID NULL,
    usuario_id         INTEGER NULL,
    tipo               TEXT NOT NULL,
    -- erro: frase do provedor; sem_funcionalidade: a pergunta da pessoa; chamado_enviado: id do chamado.
    detalhe            TEXT NULL,
    -- sem_funcionalidade: título do rascunho; erro: provedor.
    extra              TEXT NULL,
    -- Dia local (São Paulo) do evento; usado no "um bloqueio por loja por dia".
    dia                DATE NOT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE assistente_eventos DROP CONSTRAINT IF EXISTS ck_assistente_eventos_tipo;
ALTER TABLE assistente_eventos ADD CONSTRAINT ck_assistente_eventos_tipo
    CHECK (tipo IN ('erro', 'limite', 'chamado_enviado', 'sem_funcionalidade'));
CREATE INDEX IF NOT EXISTS ix_assistente_eventos_tipo ON assistente_eventos (tipo, criado_em);
-- Um bloqueio por loja por dia basta para "dias no limite".
CREATE UNIQUE INDEX IF NOT EXISTS ux_assistente_eventos_limite_dia
    ON assistente_eventos (estabelecimento_id, dia) WHERE tipo = 'limite';

ALTER TABLE assistente_config ADD COLUMN IF NOT EXISTS precos JSONB NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_05_assistente_uso')
ON CONFLICT (version) DO NOTHING;

COMMIT;
