-- Conexao do WhatsApp autorizada pela propria Meta (Embedded Signup v4 no app central ZippyGo, como Tech Provider).
-- * whatsapp_autorizacao_meta: o que a loja autorizou (WABA, business, token de integracao cifrado, permissoes). Uma WABA pode ser
--   autorizada por mais de uma loja (WABA compartilhada): cada loja tem a sua linha e so os numeros DELA ficam ligados a ela.
-- * whatsapp_conexao_tentativa: cada "Conectar com a Meta" iniciado no painel (state contra CSRF, expiracao, etapa, erro). Tentativa
--   incompleta nunca vira canal ativo.
-- * canal_whatsapp ganha a origem 'autorizado' (token da autorizacao, webhook GLOBAL do app ZippyGo) e o estado do registro do numero.
-- * wa_evento aceita 'conta' (account_update: PARTNER_REMOVED etc.) e 'eco' (smb_message_echoes da coexistencia).
-- Aditiva e idempotente: nao reatribui numeros, nao apaga credenciais e nao muda canais zippy/proprio/central existentes.
-- Rollback em rollback/20261030_01_whatsapp_conexao_meta.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('canal_whatsapp') IS NULL OR to_regclass('wa_evento') IS NULL THEN
        RAISE EXCEPTION 'whatsapp_conexao_meta requer canal_whatsapp e wa_evento';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = 'origem') THEN
        RAISE EXCEPTION 'whatsapp_conexao_meta requer canal_whatsapp.origem (20261025_01_canal_origem_proprio)';
    END IF;
END $$;

-- 1) Autorizacao concedida pela loja ao app integrador ------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS whatsapp_autorizacao_meta (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    -- App integrador (ZippyGo). Gravado para nunca usar o token com outro app.
    app_id TEXT NOT NULL CHECK (app_id ~ '^[0-9]+$'),
    waba_id TEXT NOT NULL CHECK (waba_id ~ '^[0-9]+$'),
    -- Portfolio dono da WABA. Confirmado = veio da Meta (owner_business_info), nao so do navegador.
    business_id TEXT NULL CHECK (business_id IS NULL OR business_id ~ '^[0-9]+$'),
    business_id_confirmado BOOLEAN NOT NULL DEFAULT FALSE,
    -- Token de integracao (business integration system user), cifrado pela aplicacao. Nunca sai em resposta.
    token_cifrado TEXT NULL,
    token_expira_em TIMESTAMPTZ NULL,
    permissoes TEXT[] NOT NULL DEFAULT '{}',
    estado TEXT NOT NULL DEFAULT 'ativa' CHECK (estado IN ('ativa', 'revogada', 'expirada', 'erro')),
    -- FINISH, FINISH_ONLY_WABA, FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING...
    tipo_fluxo TEXT NULL,
    ultimo_erro TEXT NULL,
    validada_em TIMESTAMPTZ NULL,
    revogada_em TIMESTAMPTZ NULL,
    criado_por INTEGER NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_whatsapp_autorizacao_loja_app_waba UNIQUE (id_estabelecimento, app_id, waba_id)
);

CREATE INDEX IF NOT EXISTS ix_whatsapp_autorizacao_waba ON whatsapp_autorizacao_meta (waba_id);

-- 2) Tentativas de conexao -----------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS whatsapp_conexao_tentativa (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    usuario_id INTEGER NOT NULL,
    -- SHA-256 do state entregue ao navegador (o state em si nunca e gravado).
    state_hash TEXT NOT NULL,
    modo TEXT NOT NULL DEFAULT 'conectar' CHECK (modo IN ('conectar', 'reconectar')),
    -- Reconectar: o canal que deve ser mantido (mesmo id = mesmo historico).
    id_canal UUID NULL REFERENCES canal_whatsapp (id) ON DELETE SET NULL,
    estado TEXT NOT NULL DEFAULT 'iniciada'
        CHECK (estado IN ('iniciada', 'trocando', 'autorizada', 'ativando', 'concluida', 'cancelada', 'expirada', 'erro', 'substituida')),
    etapa TEXT NULL,
    evento_fluxo TEXT NULL,
    coexistencia BOOLEAN NOT NULL DEFAULT FALSE,
    id_autorizacao UUID NULL REFERENCES whatsapp_autorizacao_meta (id) ON DELETE SET NULL,
    waba_id TEXT NULL,
    business_id TEXT NULL,
    phone_number_id TEXT NULL,
    -- Numeros da WABA autorizada, como a Meta devolveu (sem segredos), para a escolha quando ha mais de um.
    numeros JSONB NOT NULL DEFAULT '[]'::jsonb,
    erro_codigo TEXT NULL,
    erro_mensagem TEXT NULL,
    -- Prazo do state (assinar na Meta) e, depois de autorizada, prazo para escolher/ativar o numero.
    expira_em TIMESTAMPTZ NOT NULL,
    concluida_em TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whatsapp_conexao_tentativa_loja ON whatsapp_conexao_tentativa (id_estabelecimento, created_at_utc DESC);
-- No maximo uma troca de codigo em andamento por loja (concorrencia entre abas).
CREATE UNIQUE INDEX IF NOT EXISTS ux_whatsapp_conexao_tentativa_trocando
    ON whatsapp_conexao_tentativa (id_estabelecimento) WHERE estado = 'trocando';

-- 3) Canal: origem 'autorizado' + registro do numero -----------------------------------------------------------------------
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS id_autorizacao_meta UUID NULL REFERENCES whatsapp_autorizacao_meta (id) ON DELETE SET NULL;
-- Numero tambem em uso no app WhatsApp Business (coexistencia): nunca registrar/desregistrar pela API.
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS coexistencia BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS registro_estado TEXT NOT NULL DEFAULT 'nao_aplicavel';
-- PIN de duas etapas usado no POST /{phone}/register, cifrado (necessario para registrar de novo).
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS registro_pin_cifrado TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS registro_tentativas INTEGER NOT NULL DEFAULT 0;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS registro_tentado_em TIMESTAMPTZ NULL;
-- status do numero na Meta (CONNECTED, PENDING, ...) na ultima verificacao.
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS estado_meta TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS inscricao_waba_em TIMESTAMPTZ NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS coexistencia_sincronizada_em TIMESTAMPTZ NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS desconectado_em TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_canal_whatsapp_autorizacao ON canal_whatsapp (id_autorizacao_meta) WHERE id_autorizacao_meta IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_canal_whatsapp_waba ON canal_whatsapp (waba_id) WHERE waba_id IS NOT NULL;

DO $$
DECLARE
    restricao TEXT;
BEGIN
    -- Origem: amplia a lista (zippy, proprio, central continuam valendo).
    ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_origem;
    ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_origem CHECK (origem IN ('zippy', 'proprio', 'central', 'autorizado'));

    -- Estado do webhook vale para 'proprio' (webhook do app do cliente) e 'autorizado' (webhook global, so estado);
    -- a chave de URL por canal continua so do 'proprio'.
    ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_webhook_origem;
    ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_webhook_origem
        CHECK ((origem IN ('proprio', 'autorizado') OR webhook_estado = 'nao_aplicavel') AND (origem = 'proprio' OR webhook_chave IS NULL));

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_registro_estado' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_registro_estado
            CHECK (registro_estado IN ('nao_aplicavel', 'pendente', 'registrado', 'dispensado', 'erro'));
    END IF;

    -- wa_evento: o CHECK de tipo foi criado sem nome; troca pelo nomeado com os tipos novos.
    FOR restricao IN
        SELECT conname FROM pg_constraint
         WHERE conrelid = 'wa_evento'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%tipo%'
           AND conname <> 'ck_wa_evento_tipo'
    LOOP
        EXECUTE format('ALTER TABLE wa_evento DROP CONSTRAINT %I', restricao);
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_tipo' AND conrelid = 'wa_evento'::regclass) THEN
        ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_tipo CHECK (tipo IN ('mensagem', 'status', 'conta', 'eco'));
    END IF;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261030_01_whatsapp_conexao_meta')
ON CONFLICT (version) DO NOTHING;

COMMIT;
