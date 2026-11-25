-- Volta a restricao de subtipo. So roda se nenhuma conta de adiantamentos foi criada.
BEGIN;

ALTER TABLE gestao_contas DROP CONSTRAINT IF EXISTS gestao_contas_subtipo_check;
ALTER TABLE gestao_contas ADD CONSTRAINT gestao_contas_subtipo_check
    CHECK (subtipo IN ('ifood', 'maquininha', 'motoboys', 'outro'));

COMMIT;
