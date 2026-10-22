BEGIN;

-- So volta enquanto nenhum cliente conectou numero proprio: apagar as colunas perderia o app secret, o verify token e a chave
-- do webhook (o cliente teria de conectar de novo). Com numero proprio em uso, desconecte-os pela API antes.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM canal_whatsapp WHERE origem = 'proprio') THEN
        RAISE EXCEPTION 'Ha canais com origem proprio: desconecte-os antes de reverter 20261025_01_canal_origem_proprio';
    END IF;
END $$;

DROP INDEX IF EXISTS ux_canal_whatsapp_webhook_chave;
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_webhook_origem;
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_webhook_chave;
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_webhook_estado;
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_origem;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS webhook_registrado_em;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS webhook_erro;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS webhook_estado;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS verify_token_cifrado;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS webhook_chave;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS app_secret_cifrado;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS app_id;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS waba_id;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS origem;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261025_01_canal_origem_proprio';
COMMIT;
