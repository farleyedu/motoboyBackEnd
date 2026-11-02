-- Desfaz 20261123_01_gestao_caixa_aprovacao.sql. Fechamento enviado e ainda nao aprovado volta a ser caixa aberto (o operador conta de novo).
BEGIN;
DROP INDEX IF EXISTS ix_gestao_caixa_dias_aguardando;
ALTER TABLE gestao_caixa_dias
    DROP COLUMN IF EXISTS devolucao_motivo,
    DROP COLUMN IF EXISTS devolvido_por,
    DROP COLUMN IF EXISTS devolvido_em,
    DROP COLUMN IF EXISTS aprovado_por,
    DROP COLUMN IF EXISTS envio,
    DROP COLUMN IF EXISTS envio_por,
    DROP COLUMN IF EXISTS envio_em;
DROP TABLE IF EXISTS gestao_caixa_config;
COMMIT;
