DO $$
BEGIN
    IF to_regclass('delivery_rider_plan_resets') IS NULL THEN
        RAISE EXCEPTION 'Falta a tabela delivery_rider_plan_resets (20261026_01_remuneracao_padrao)';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name = 'delivery_rider_pay_plans' AND column_name = 'motoboy_id' AND is_nullable = 'NO') THEN
        RAISE EXCEPTION 'delivery_rider_pay_plans.motoboy_id ainda e NOT NULL: o padrao da loja nao tem motoboy';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_rider_default_plans') THEN
        RAISE EXCEPTION 'Falta o indice do historico do padrao (ix_rider_default_plans)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_rider_plan_resets') THEN
        RAISE EXCEPTION 'Falta o indice de ix_rider_plan_resets';
    END IF;
    -- A regra de remuneracao (modo/valor) vale tambem para o padrao.
    IF EXISTS (SELECT 1 FROM delivery_rider_pay_plans WHERE rate <= 0 OR rate > 1000000) THEN
        RAISE EXCEPTION 'Plano de remuneracao com valor fora do permitido';
    END IF;
    -- Lancamento sempre aponta para um plano que existe (padrao ou excecao).
    IF EXISTS (SELECT 1 FROM delivery_rider_work_entries w
                WHERE w.plan_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM delivery_rider_pay_plans p WHERE p.id = w.plan_id)) THEN
        RAISE EXCEPTION 'Lancamento de remuneracao aponta para plano inexistente';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version = '20261026_01_remuneracao_padrao';
