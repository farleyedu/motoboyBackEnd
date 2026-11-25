-- Confere 20261101_40_assinaturas (somente leitura; nao e aplicado no boot).
DO $$
DECLARE
    tabela TEXT;
BEGIN
    FOREACH tabela IN ARRAY ARRAY['assinatura', 'assinatura_ciclo', 'comercial_cobranca', 'compra_extra', 'solicitacao_compra',
                                  'comercial_aviso', 'vw_assinatura_resumo'] LOOP
        IF to_regclass(tabela) IS NULL THEN
            RAISE EXCEPTION 'Falta %', tabela;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_assinatura_viva') THEN
        RAISE EXCEPTION 'Falta a unicidade da assinatura viva por loja';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_comercial_cobranca_ciclo') THEN
        RAISE EXCEPTION 'Falta a unicidade da cobranca por ciclo';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'motoboy_pacotes' AND column_name = 'id_compra_extra') THEN
        RAISE EXCEPTION 'motoboy_pacotes sem id_compra_extra';
    END IF;
END $$;
