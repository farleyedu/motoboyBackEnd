DO $$
DECLARE
    coluna TEXT;
BEGIN
    IF to_regclass('whatsapp_template') IS NULL THEN
        RAISE EXCEPTION 'Falta whatsapp_template';
    END IF;
    IF to_regclass('whatsapp_template_evento') IS NULL THEN
        RAISE EXCEPTION 'Falta whatsapp_template_evento';
    END IF;
    FOREACH coluna IN ARRAY ARRAY['id_estabelecimento', 'id_canal', 'waba_id', 'meta_template_id', 'nome', 'idioma', 'categoria',
                                  'categoria_meta', 'conteudo', 'componentes_meta', 'situacao', 'status_meta', 'motivo_rejeicao',
                                  'criado_por', 'sincronizado_em', 'excluido_em'] LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'whatsapp_template' AND column_name = coluna) THEN
            RAISE EXCEPTION 'Falta a coluna whatsapp_template.%', coluna;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_whatsapp_template_meta_id') THEN
        RAISE EXCEPTION 'Falta a chave unica do ID da Meta';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_whatsapp_template_nome_idioma') THEN
        RAISE EXCEPTION 'Falta a chave unica de nome + idioma por WABA';
    END IF;
    IF EXISTS (SELECT 1 FROM whatsapp_template WHERE situacao = 'enviado' AND meta_template_id IS NULL) THEN
        RAISE EXCEPTION 'Modelo enviado sem ID da Meta';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version = '20261029_01_whatsapp_templates';
