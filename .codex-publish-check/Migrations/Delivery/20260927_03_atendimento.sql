BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 5 (atendimento ligado ao pedido): configuracao do atendimento por estabelecimento,
-- respostas rapidas com variaveis e mensagens internas atendente <-> motoboy presas ao pedido.
-- Tudo aditivo e idempotente. As tabelas de conversa/mensagem do WhatsApp nao sao tocadas.
-- ---------------------------------------------------------------------------

-- 1) Modo de atendimento: 'humano' (padrao) ou 'ia' (so aceito quando o modulo de IA existir, etapa 2).
CREATE TABLE IF NOT EXISTS estabelecimento_atendimento_config (
    estabelecimento_id UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    modo TEXT NOT NULL DEFAULT 'humano' CHECK (modo IN ('humano', 'ia')),
    saudacao_humano TEXT NULL,
    mensagem_fora_horario TEXT NULL,
    -- {"dias":[{"dia":1,"abre":"11:00","fecha":"23:00"}, ...]} (dia 0 = domingo); nulo = sempre aberto.
    horario_atendimento JSONB NULL,
    updated_by_user_id INTEGER NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 2) Respostas rapidas com variaveis ({numero}, {cliente}, {motoboy}, {previsao}, {total}, {loja}).
CREATE TABLE IF NOT EXISTS atendimento_respostas_rapidas (
    id UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    titulo TEXT NOT NULL,
    atalho TEXT NULL,
    texto TEXT NOT NULL,
    ordem INTEGER NOT NULL DEFAULT 0,
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_atendimento_respostas_rapidas_estab
    ON atendimento_respostas_rapidas (estabelecimento_id, ativo, ordem);

-- 3) Mensagens internas atendente <-> motoboy, presas ao pedido (o app do motoboy consome na etapa 2).
CREATE TABLE IF NOT EXISTS delivery_motoboy_message (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy (id),
    pedido_id INTEGER NULL REFERENCES pedido (id),
    direction TEXT NOT NULL CHECK (direction IN ('operator', 'motoboy')),
    body TEXT NOT NULL CHECK (char_length(body) BETWEEN 1 AND 500),
    quick_key TEXT NULL,
    sent_by_user_id INTEGER NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    read_at_utc TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_delivery_motoboy_message_motoboy
    ON delivery_motoboy_message (estabelecimento_id, motoboy_id, created_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_delivery_motoboy_message_pedido
    ON delivery_motoboy_message (pedido_id, created_at_utc DESC) WHERE pedido_id IS NOT NULL;

-- 4) Vinculo pedido <-> conversa por consulta rapida (a coluna pedido.conversa_id veio na Fase 2).
CREATE INDEX IF NOT EXISTS ix_pedido_conversa_id ON pedido (conversa_id) WHERE conversa_id IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260927_03_atendimento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
