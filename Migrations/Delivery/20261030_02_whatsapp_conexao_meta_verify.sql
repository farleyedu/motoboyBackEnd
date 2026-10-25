DO $$
DECLARE
    coluna TEXT;
BEGIN
    IF to_regclass('whatsapp_autorizacao_meta') IS NULL THEN
        RAISE EXCEPTION 'Falta whatsapp_autorizacao_meta';
    END IF;
    IF to_regclass('whatsapp_conexao_tentativa') IS NULL THEN
        RAISE EXCEPTION 'Falta whatsapp_conexao_tentativa';
    END IF;
    FOREACH coluna IN ARRAY ARRAY['id_autorizacao_meta', 'coexistencia', 'registro_estado', 'registro_pin_cifrado', 'registro_tentativas',
                                  'registro_tentado_em', 'estado_meta', 'inscricao_waba_em', 'coexistencia_sincronizada_em', 'desconectado_em'] LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = coluna) THEN
            RAISE EXCEPTION 'Falta a coluna canal_whatsapp.%', coluna;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_origem'
                     AND pg_get_constraintdef(oid) LIKE '%autorizado%') THEN
        RAISE EXCEPTION 'CHECK de origem sem autorizado';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_tipo' AND pg_get_constraintdef(oid) LIKE '%eco%') THEN
        RAISE EXCEPTION 'CHECK de wa_evento.tipo sem conta/eco';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_canal_whatsapp_phone_number_id') THEN
        RAISE EXCEPTION 'Sumiu a unicidade do Phone Number ID';
    END IF;
    IF EXISTS (SELECT 1 FROM canal_whatsapp WHERE origem <> 'proprio' AND webhook_chave IS NOT NULL) THEN
        RAISE EXCEPTION 'Webhook por canal em canal que nao e proprio';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version = '20261030_01_whatsapp_conexao_meta';
