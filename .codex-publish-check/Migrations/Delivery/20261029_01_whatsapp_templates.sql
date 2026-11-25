-- Modelos oficiais de mensagem do WhatsApp (templates da Meta) cadastrados pelo Zippy. O catalogo remoto pertence a WABA, que pode
-- ser compartilhada por varias lojas (numeros da Zippy e o central de testes): esta tabela e o vinculo explicito entre um modelo da
-- Meta e a loja que o criou. Modelos da WABA que nao estao aqui nao sao de ninguem no Zippy e nunca sao alterados nem adotados.
-- A Meta e a fonte de verdade do status (status_meta); "situacao" e so o estado local do envio (enviando/verificando/enviado/...).
-- Aditiva e idempotente. Rollback em rollback/20261029_01_whatsapp_templates.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('canal_whatsapp') IS NULL THEN
        RAISE EXCEPTION 'whatsapp_templates requer canal_whatsapp (20261002_07_catalogo_servicos_canais)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = 'waba_id') THEN
        RAISE EXCEPTION 'whatsapp_templates requer canal_whatsapp.waba_id (20261025_01_canal_origem_proprio)';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS whatsapp_template (
    id UUID PRIMARY KEY,
    -- Loja dona do modelo: so ela (e a Gestao) enxerga e mexe nele, mesmo numa WABA compartilhada.
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    -- Numero pelo qual o modelo foi criado. O modelo e da WABA, entao sobrevive ao numero (fica so o vinculo com a loja).
    id_canal UUID NULL REFERENCES canal_whatsapp (id) ON DELETE SET NULL,
    waba_id TEXT NOT NULL CHECK (waba_id ~ '^[0-9]+$'),
    -- ID do modelo na Meta (hsm_id). Nulo enquanto a Meta nao confirmou a criacao.
    meta_template_id TEXT NULL CHECK (meta_template_id IS NULL OR meta_template_id ~ '^[0-9]+$'),
    nome TEXT NOT NULL CHECK (nome ~ '^[a-z0-9_]{1,512}$'),
    idioma TEXT NOT NULL CHECK (idioma ~ '^[a-z]{2,3}(_[A-Z]{2})?$'),
    -- Categoria pedida; a Meta pode reclassificar (categoria_meta).
    categoria TEXT NOT NULL CHECK (categoria IN ('UTILITY', 'MARKETING')),
    categoria_meta TEXT NULL,
    -- Conteudo enviado (cabecalho, corpo, rodape, botoes e exemplos), no formato do Zippy.
    conteudo JSONB NOT NULL,
    -- Componentes como a Meta devolveu na ultima sincronizacao.
    componentes_meta JSONB NULL,
    situacao TEXT NOT NULL CHECK (situacao IN ('enviando', 'verificando', 'enviado', 'falha', 'excluido')),
    status_meta TEXT NULL,
    motivo_rejeicao TEXT NULL,
    qualidade TEXT NULL,
    ultimo_erro TEXT NULL,
    ultimo_erro_codigo TEXT NULL,
    criado_por INTEGER NULL,
    enviado_em TIMESTAMPTZ NULL,
    status_alterado_em TIMESTAMPTZ NULL,
    sincronizado_em TIMESTAMPTZ NULL,
    excluido_em TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_whatsapp_template_enviado_tem_id CHECK (situacao <> 'enviado' OR meta_template_id IS NOT NULL)
);

-- Um modelo da Meta pertence a uma loja so.
CREATE UNIQUE INDEX IF NOT EXISTS ux_whatsapp_template_meta_id
    ON whatsapp_template (waba_id, meta_template_id) WHERE meta_template_id IS NOT NULL;
-- Nome + idioma e unico na WABA (regra da Meta). Bloqueia envio duplo enquanto um envio esta em curso ou sem resposta.
CREATE UNIQUE INDEX IF NOT EXISTS ux_whatsapp_template_nome_idioma
    ON whatsapp_template (waba_id, nome, idioma) WHERE situacao IN ('enviando', 'verificando', 'enviado');
CREATE INDEX IF NOT EXISTS ix_whatsapp_template_loja ON whatsapp_template (id_estabelecimento, waba_id, created_at_utc DESC);

-- Historico de tudo o que aconteceu com o modelo: criacao, envio, mudanca de status, erros e exclusao.
CREATE TABLE IF NOT EXISTS whatsapp_template_evento (
    id BIGSERIAL PRIMARY KEY,
    id_template UUID NOT NULL REFERENCES whatsapp_template (id) ON DELETE CASCADE,
    acao TEXT NOT NULL,
    usuario_id INTEGER NULL,
    detalhe JSONB NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_whatsapp_template_evento_template ON whatsapp_template_evento (id_template, id);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261029_01_whatsapp_templates')
ON CONFLICT (version) DO NOTHING;

COMMIT;
