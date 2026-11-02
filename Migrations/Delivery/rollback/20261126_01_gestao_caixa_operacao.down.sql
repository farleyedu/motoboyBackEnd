-- Desfaz 20261126_01_gestao_caixa_operacao.sql. O dia volta a ser o do calendario e nenhum fechamento espera aprovacao para abrir.
BEGIN;
DROP INDEX IF EXISTS ix_gestao_caixa_dias_sem_aprovacao;
ALTER TABLE gestao_caixa_turnos DROP COLUMN IF EXISTS aprovado_em;
ALTER TABLE gestao_caixa_dias DROP COLUMN IF EXISTS aprovado_em;
ALTER TABLE gestao_caixa_config DROP CONSTRAINT IF EXISTS ck_gestao_caixa_config_virada;
ALTER TABLE gestao_caixa_config DROP COLUMN IF EXISTS virada_do_dia;
COMMIT;
