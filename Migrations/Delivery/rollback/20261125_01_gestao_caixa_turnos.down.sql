-- Desfaz 20261125_01_gestao_caixa_turnos.sql. Os fechamentos guardados em gestao_caixa_turnos somem (o livro continua com tudo:
-- quebras, recolhimentos e estornos). Dia que estava no 2o turno ou reaberto fica como esta; so deixa de saber o que veio antes.
BEGIN;
DROP TABLE IF EXISTS gestao_caixa_turnos;
ALTER TABLE gestao_caixa_dias
    DROP COLUMN IF EXISTS ciclo_desde,
    DROP COLUMN IF EXISTS turno,
    DROP COLUMN IF EXISTS versao;
COMMIT;
