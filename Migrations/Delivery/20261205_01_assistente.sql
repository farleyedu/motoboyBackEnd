BEGIN;

-- Assistente da Zippy (zippy-admin/docs/assistente-ajuda/ANALISE.md e CONTRATO-API.md).
-- Conversa e mensagens ficam 30 dias (AssistenteLimpeza apaga); ação pendente e auditoria ficam.
-- A IA nunca executa: propor_acao grava em assistente_acoes (pendente, 10 min) e quem executa é o confirmar, com o login de quem clicou.
-- Aditiva e idempotente. Requer estabelecimentos.

CREATE TABLE IF NOT EXISTS assistente_conversas (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    usuario_id         INTEGER NOT NULL,
    criada_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizada_em      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_assistente_conversas_loja ON assistente_conversas (estabelecimento_id, atualizada_em DESC);
CREATE INDEX IF NOT EXISTS ix_assistente_conversas_atualizada ON assistente_conversas (atualizada_em);

CREATE TABLE IF NOT EXISTS assistente_mensagens (
    id                 UUID PRIMARY KEY,
    conversa_id        UUID NOT NULL REFERENCES assistente_conversas (id) ON DELETE CASCADE,
    estabelecimento_id UUID NOT NULL,
    usuario_id         INTEGER NOT NULL,
    papel              TEXT NOT NULL,
    texto              TEXT NOT NULL,
    -- Cartões no formato do CONTRATO-API §2 (só nas respostas da IA).
    cartoes            JSONB NOT NULL DEFAULT '[]'::jsonb,
    -- Uso de tokens da resposta (custo), nulo nas mensagens da pessoa.
    tokens_entrada     INTEGER NULL,
    tokens_saida       INTEGER NULL,
    tokens_cache_lidos INTEGER NULL,
    modelo             TEXT NULL,
    criada_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE assistente_mensagens DROP CONSTRAINT IF EXISTS ck_assistente_mensagens_papel;
ALTER TABLE assistente_mensagens ADD CONSTRAINT ck_assistente_mensagens_papel CHECK (papel IN ('usuario', 'assistente'));
CREATE INDEX IF NOT EXISTS ix_assistente_mensagens_conversa ON assistente_mensagens (conversa_id, criada_em);
-- Limite diário: conta as mensagens da pessoa por loja desde a meia-noite local.
CREATE INDEX IF NOT EXISTS ix_assistente_mensagens_limite ON assistente_mensagens (estabelecimento_id, criada_em) WHERE papel = 'usuario';

CREATE TABLE IF NOT EXISTS assistente_acoes (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    usuario_id         INTEGER NOT NULL,
    conversa_id        UUID NULL REFERENCES assistente_conversas (id) ON DELETE SET NULL,
    funcionalidade_id  TEXT NOT NULL,
    acao               TEXT NOT NULL,
    parametros         JSONB NOT NULL,
    resumo             TEXT NOT NULL,
    antes              TEXT NULL,
    depois             TEXT NULL,
    situacao           TEXT NOT NULL DEFAULT 'pendente',
    expira_em          TIMESTAMPTZ NOT NULL,
    criada_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    decidida_em        TIMESTAMPTZ NULL,
    decidida_por       INTEGER NULL,
    -- Ação criada como "desfazer" de outra já executada.
    desfaz_acao_id     UUID NULL REFERENCES assistente_acoes (id),
    erro               TEXT NULL
);
ALTER TABLE assistente_acoes DROP CONSTRAINT IF EXISTS ck_assistente_acoes_situacao;
ALTER TABLE assistente_acoes ADD CONSTRAINT ck_assistente_acoes_situacao
    CHECK (situacao IN ('pendente', 'executando', 'executada', 'recusada', 'falhou'));
ALTER TABLE assistente_acoes DROP CONSTRAINT IF EXISTS ck_assistente_acoes_decisao;
ALTER TABLE assistente_acoes ADD CONSTRAINT ck_assistente_acoes_decisao
    CHECK ((situacao = 'pendente') = (decidida_em IS NULL));
CREATE INDEX IF NOT EXISTS ix_assistente_acoes_loja ON assistente_acoes (estabelecimento_id, criada_em DESC);

-- Registro de toda ação decidida (quem, quando, antes, depois, conversa). Não é apagado com a conversa.
CREATE TABLE IF NOT EXISTS assistente_auditoria (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    usuario_id         INTEGER NOT NULL,
    acao_id            UUID NOT NULL REFERENCES assistente_acoes (id),
    conversa_id        UUID NULL,
    funcionalidade_id  TEXT NOT NULL,
    acao               TEXT NOT NULL,
    parametros         JSONB NOT NULL,
    antes              TEXT NULL,
    depois             TEXT NULL,
    resultado          TEXT NOT NULL,
    detalhe            TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE assistente_auditoria DROP CONSTRAINT IF EXISTS ck_assistente_auditoria_resultado;
ALTER TABLE assistente_auditoria ADD CONSTRAINT ck_assistente_auditoria_resultado
    CHECK (resultado IN ('executada', 'recusada', 'falhou'));
CREATE INDEX IF NOT EXISTS ix_assistente_auditoria_loja ON assistente_auditoria (estabelecimento_id, criado_em DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_01_assistente')
ON CONFLICT (version) DO NOTHING;

COMMIT;
