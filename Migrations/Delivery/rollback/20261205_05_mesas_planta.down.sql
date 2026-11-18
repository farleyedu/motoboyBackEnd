BEGIN;

-- Recusa se ja existe mesa publicada: sessoes, pedidos e pagamentos apontam para ela.
DO $$
BEGIN
    IF to_regclass('mesa') IS NOT NULL AND EXISTS (SELECT 1 FROM mesa) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem mesas publicadas.';
    END IF;
END $$;

DROP TABLE IF EXISTS mesa_estado;
DROP TABLE IF EXISTS mesa;
DROP TABLE IF EXISTS mesa_area;
DROP TABLE IF EXISTS mesa_andar;
-- Rascunhos sem mesa publicada podem sair (a trigger impede DELETE linha a linha, DROP TABLE nao passa por ela).
DROP TABLE IF EXISTS mesa_planta;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261205_05_mesas_planta';

COMMIT;
