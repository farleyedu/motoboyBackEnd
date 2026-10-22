-- Remuneracao dos motoboys: regra PADRAO da loja + excecao por motoboy.
--   * Padrao da loja = linha de delivery_rider_pay_plans com motoboy_id NULL (mesmos modos, validacoes e historico versionado:
--     cada mudanca e uma linha nova, nunca um UPDATE). Assim delivery_rider_work_entries.plan_id continua apontando para o plano
--     exato com que cada lancamento foi calculado, seja ele padrao ou excecao.
--   * Excecao por motoboy = linha com motoboy_id preenchido (como era).
--   * "Voltar ao padrao" = evento em delivery_rider_plan_resets. Nada e apagado: a regra efetiva do motoboy e o evento mais recente
--     dele (excecao ou volta ao padrao); se for a volta ao padrao, ou se ele nunca teve excecao, vale o padrao vigente da loja.
-- Aditiva e idempotente. Rollback em rollback/20261026_01_remuneracao_padrao.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('delivery_rider_pay_plans') IS NULL THEN
        RAISE EXCEPTION 'remuneracao_padrao requer a remuneracao dos motoboys (20261008_08_motoboy_work)';
    END IF;
END $$;

-- Padrao da loja: sem motoboy. A FK de work_entries.plan_id continua valendo para os dois tipos de plano.
ALTER TABLE delivery_rider_pay_plans ALTER COLUMN motoboy_id DROP NOT NULL;

-- Historico do padrao da loja, mais recente primeiro.
CREATE INDEX IF NOT EXISTS ix_rider_default_plans
    ON delivery_rider_pay_plans (estabelecimento_id, created_at_utc DESC, id DESC) WHERE motoboy_id IS NULL;

-- "Voltar ao padrao": o id e a OperationId do pedido (reenviar nao duplica).
CREATE TABLE IF NOT EXISTS delivery_rider_plan_resets (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL,
    motoboy_id         INTEGER NOT NULL,
    created_by         INTEGER NOT NULL,
    created_at_utc     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_rider_plan_resets
    ON delivery_rider_plan_resets (estabelecimento_id, motoboy_id, created_at_utc DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261026_01_remuneracao_padrao')
ON CONFLICT (version) DO NOTHING;

COMMIT;
