-- Origem do numero de WhatsApp: 'zippy' (numero na conta Meta da Zippy, token global), 'proprio' (o cliente usa o Meta dele e
-- conecta sozinho; webhook proprio por canal em /wa/webhook/{chave}, assinado com o app secret DO CLIENTE) e 'central'
-- (numero compartilhado de testes). Segredos (app secret, verify token) ficam cifrados como o token (ITokenProtector).
-- Aditiva e idempotente. Rollback em rollback/20261025_01_canal_origem_proprio.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('canal_whatsapp') IS NULL THEN
        RAISE EXCEPTION 'canal_origem_proprio requer canal_whatsapp (20261002_07_catalogo_servicos_canais)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = 'central') THEN
        RAISE EXCEPTION 'canal_origem_proprio requer a coluna central (20261020_01_canal_central)';
    END IF;
END $$;

ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS origem TEXT NOT NULL DEFAULT 'zippy';
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS waba_id TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS app_id TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS app_secret_cifrado TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS webhook_chave TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS verify_token_cifrado TEXT NULL;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS webhook_estado TEXT NOT NULL DEFAULT 'nao_aplicavel';
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS webhook_erro TEXT NULL;
-- Quando o webhook foi aceito pela Meta (automatico ou manual); nao faz parte do contrato minimo, mas a tela mostra.
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS webhook_registrado_em TIMESTAMPTZ NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_origem' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_origem CHECK (origem IN ('zippy', 'proprio', 'central'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_webhook_estado' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_webhook_estado
            CHECK (webhook_estado IN ('nao_aplicavel', 'pendente', 'registrado', 'recebendo', 'erro'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_webhook_chave' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_webhook_chave
            CHECK (webhook_chave IS NULL OR length(webhook_chave) >= 32);
    END IF;
    -- So o numero proprio tem webhook proprio.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_webhook_origem' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_webhook_origem
            CHECK (origem = 'proprio' OR (webhook_estado = 'nao_aplicavel' AND webhook_chave IS NULL));
    END IF;
END $$;

-- Backfill: o central de testes ja existente; os demais continuam 'zippy' (o default).
UPDATE canal_whatsapp SET origem = 'central' WHERE central AND origem <> 'central';

CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_webhook_chave ON canal_whatsapp (webhook_chave) WHERE webhook_chave IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261025_01_canal_origem_proprio')
ON CONFLICT (version) DO NOTHING;

COMMIT;
