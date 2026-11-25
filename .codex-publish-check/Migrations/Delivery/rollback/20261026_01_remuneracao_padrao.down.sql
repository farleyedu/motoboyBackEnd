BEGIN;

-- Os planos PADRAO da loja (motoboy_id NULL) so existem por esta migration. Antes de voltar motoboy_id para NOT NULL e preciso
-- tirar esses planos; como lancamentos ja calculados podem apontar para eles, o rollback para se houver algum em uso.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM delivery_rider_work_entries w
                JOIN delivery_rider_pay_plans p ON p.id = w.plan_id WHERE p.motoboy_id IS NULL) THEN
        RAISE EXCEPTION 'Ha lancamentos calculados com a remuneracao padrao da loja: nao e seguro desfazer';
    END IF;
END $$;

DROP TABLE IF EXISTS delivery_rider_plan_resets;
DROP INDEX IF EXISTS ix_rider_default_plans;
DELETE FROM delivery_rider_pay_plans WHERE motoboy_id IS NULL;
ALTER TABLE delivery_rider_pay_plans ALTER COLUMN motoboy_id SET NOT NULL;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261026_01_remuneracao_padrao';
COMMIT;
