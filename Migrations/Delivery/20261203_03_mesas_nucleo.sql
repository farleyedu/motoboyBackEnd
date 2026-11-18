BEGIN;

-- Mesas, P01 (CONTRATO-API §1.1): configuracao da loja, historico imutavel da configuracao, funcao canonica da equipe,
-- idempotencia de comando e livro de eventos do salao. Requer as migracoes da Gestao (gestao_pessoas, gestao_bloqueia_alteracao).
-- Aditiva e idempotente.

-- Uma linha por loja. dados guarda so o que a loja mudou; chave ausente = padrao do servidor (MesasConfigPadroes).
CREATE TABLE IF NOT EXISTS mesas_config (
    estabelecimento_id UUID PRIMARY KEY REFERENCES estabelecimentos (id),
    ativo              BOOLEAN NOT NULL DEFAULT FALSE,
    dados              JSONB NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(dados) = 'object'),
    versao             INTEGER NOT NULL DEFAULT 1 CHECK (versao > 0),
    atualizado_por     INTEGER NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Cada mudanca de configuracao: chave (caminho com ponto), antes e depois, rotulo legivel e autor. Nunca muda nem some.
CREATE TABLE IF NOT EXISTS mesas_config_historico (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    chave              TEXT NOT NULL CHECK (length(chave) BETWEEN 1 AND 120),
    antes              JSONB NULL,
    depois             JSONB NULL,
    rotulo             TEXT NULL CHECK (rotulo IS NULL OR length(rotulo) <= 200),
    versao             INTEGER NOT NULL CHECK (versao > 0),
    por                INTEGER NULL,
    quando             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_mesas_config_historico_loja ON mesas_config_historico (estabelecimento_id, quando DESC);
DROP TRIGGER IF EXISTS mesas_config_historico_imutavel ON mesas_config_historico;
CREATE TRIGGER mesas_config_historico_imutavel BEFORE UPDATE OR DELETE ON mesas_config_historico
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

-- Funcao canonica da pessoa na equipe. 'funcao' (texto livre) continua; motoboy continua vindo do vinculo.
ALTER TABLE gestao_pessoas ADD COLUMN IF NOT EXISTS funcao_codigo TEXT NULL;
ALTER TABLE gestao_pessoas DROP CONSTRAINT IF EXISTS ck_gestao_pessoas_funcao_codigo;
ALTER TABLE gestao_pessoas ADD CONSTRAINT ck_gestao_pessoas_funcao_codigo
    CHECK (funcao_codigo IS NULL OR funcao_codigo IN ('garcom', 'gerente_salao', 'cozinha', 'bar', 'caixa'));
CREATE INDEX IF NOT EXISTS ix_gestao_pessoas_funcao_codigo ON gestao_pessoas (estabelecimento_id, funcao_codigo);
CREATE INDEX IF NOT EXISTS ix_gestao_pessoas_usuario ON gestao_pessoas (usuario_id) WHERE usuario_id IS NOT NULL;

-- Idempotencia dos comandos (§0.2): mesma chave + mesmo hash devolve a resposta gravada. Guarda 48 h.
CREATE TABLE IF NOT EXISTS mesa_comando (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    chave              UUID NOT NULL,
    comando            TEXT NOT NULL CHECK (length(comando) BETWEEN 1 AND 80),
    hash               TEXT NOT NULL,
    resposta           JSONB NULL,
    status             SMALLINT NOT NULL,
    usuario_id         INTEGER NULL,
    quando             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_mesa_comando_chave UNIQUE (estabelecimento_id, chave)
);
CREATE INDEX IF NOT EXISTS ix_mesa_comando_quando ON mesa_comando (quando);

-- Livro de eventos do salao (auditoria). Imutavel.
CREATE TABLE IF NOT EXISTS mesa_evento (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    quando             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ator_id            TEXT NULL,
    ator_nome          TEXT NOT NULL CHECK (length(ator_nome) <= 80),
    ator_papel         TEXT NOT NULL CHECK (ator_papel IN ('dono', 'gerente', 'caixa', 'garcom', 'cozinha', 'cliente', 'cliente_anonimo', 'sistema')),
    acao               TEXT NOT NULL CHECK (length(acao) BETWEEN 1 AND 80),
    alvo_tipo          TEXT NULL,
    alvo_id            UUID NULL,
    alvo_rotulo        TEXT NULL,
    antes              TEXT NULL,
    depois             TEXT NULL,
    motivo             TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 280),
    mesa_id            UUID NULL,
    sessao_id          UUID NULL,
    ocorrencia         BOOLEAN NOT NULL DEFAULT FALSE,
    acontecimento_id   UUID NULL,
    comando_chave      UUID NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_mesa_evento_loja ON mesa_evento (estabelecimento_id, quando DESC);
CREATE INDEX IF NOT EXISTS ix_mesa_evento_mesa ON mesa_evento (estabelecimento_id, mesa_id, quando);
CREATE INDEX IF NOT EXISTS ix_mesa_evento_ator ON mesa_evento (estabelecimento_id, ator_id, quando);
CREATE INDEX IF NOT EXISTS ix_mesa_evento_acao ON mesa_evento (estabelecimento_id, acao);
DROP TRIGGER IF EXISTS mesa_evento_imutavel ON mesa_evento;
CREATE TRIGGER mesa_evento_imutavel BEFORE UPDATE OR DELETE ON mesa_evento
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261203_03_mesas_nucleo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
