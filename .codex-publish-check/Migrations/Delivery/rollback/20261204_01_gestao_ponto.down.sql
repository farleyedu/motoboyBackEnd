BEGIN;

-- Recusa se ja existe ponto batido: apagar perderia prova de jornada.
DO $$
BEGIN
    IF to_regclass('gestao_ponto') IS NOT NULL AND EXISTS (SELECT 1 FROM gestao_ponto) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem pontos batidos.';
    END IF;
END $$;

DROP TABLE IF EXISTS gestao_ponto_pausa;
DROP TABLE IF EXISTS gestao_ponto;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261204_01_gestao_ponto';

COMMIT;
