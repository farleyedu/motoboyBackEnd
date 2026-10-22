DO $$
DECLARE
    coluna TEXT;
BEGIN
    IF to_regclass('canal_whatsapp') IS NULL THEN
        RAISE EXCEPTION 'Falta canal_whatsapp';
    END IF;
    FOREACH coluna IN ARRAY ARRAY['origem', 'waba_id', 'app_id', 'app_secret_cifrado', 'webhook_chave', 'verify_token_cifrado',
                                  'webhook_estado', 'webhook_erro', 'webhook_registrado_em'] LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = coluna) THEN
            RAISE EXCEPTION 'Falta a coluna canal_whatsapp.%', coluna;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_origem' AND conrelid = 'canal_whatsapp'::regclass) THEN
        RAISE EXCEPTION 'Falta o CHECK de origem (zippy, proprio, central)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_webhook_estado' AND conrelid = 'canal_whatsapp'::regclass) THEN
        RAISE EXCEPTION 'Falta o CHECK de webhook_estado';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_canal_whatsapp_webhook_chave') THEN
        RAISE EXCEPTION 'Falta a chave unica do webhook por canal';
    END IF;
    IF EXISTS (SELECT 1 FROM canal_whatsapp WHERE central AND origem <> 'central') THEN
        RAISE EXCEPTION 'Canal central sem origem central';
    END IF;
    IF EXISTS (SELECT 1 FROM canal_whatsapp WHERE origem <> 'proprio' AND (webhook_chave IS NOT NULL OR webhook_estado <> 'nao_aplicavel')) THEN
        RAISE EXCEPTION 'Webhook proprio em canal que nao e proprio';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version = '20261025_01_canal_origem_proprio';
