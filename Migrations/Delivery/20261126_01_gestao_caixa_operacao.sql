-- Gestao, Caixa: dia de operacao e aprovacao antes de abrir outro caixa.
--   virada_do_dia: o dia de operacao vira a esta hora (padrao 05:00). Antes dela ainda e o dia anterior: o caixa aberto no sabado
--                  e fechado a 01:30 de domingo e o caixa de sabado. Caixa aberto continua sendo o dia em operacao ate fechar.
--   aprovado_em:   fechamento de gerente ja nasce aprovado; o de operador espera o gerente. Nenhum caixa novo abre (outro turno
--                  ou o dia seguinte) com fechamento sem aprovacao. Fechamentos que ja existiam contam como aprovados.
-- Requer 20261123_01_gestao_caixa_aprovacao.sql e 20261125_01_gestao_caixa_turnos.sql. Aditiva e idempotente.
BEGIN;

ALTER TABLE gestao_caixa_config ADD COLUMN IF NOT EXISTS virada_do_dia time NOT NULL DEFAULT '05:00';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_gestao_caixa_config_virada') THEN
        ALTER TABLE gestao_caixa_config ADD CONSTRAINT ck_gestao_caixa_config_virada CHECK (virada_do_dia BETWEEN '00:00' AND '08:00');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_schema = current_schema() AND table_name = 'gestao_caixa_dias' AND column_name = 'aprovado_em') THEN
        ALTER TABLE gestao_caixa_dias ADD COLUMN aprovado_em timestamptz;
        UPDATE gestao_caixa_dias SET aprovado_em = fechado_em WHERE fechado_em IS NOT NULL;
    END IF;
END $$;

ALTER TABLE gestao_caixa_turnos ADD COLUMN IF NOT EXISTS aprovado_em timestamptz;

CREATE INDEX IF NOT EXISTS ix_gestao_caixa_dias_sem_aprovacao ON gestao_caixa_dias (estabelecimento_id, dia)
    WHERE fechado_em IS NOT NULL AND aprovado_em IS NULL;

COMMIT;
