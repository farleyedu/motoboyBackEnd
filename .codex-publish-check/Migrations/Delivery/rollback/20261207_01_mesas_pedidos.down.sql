BEGIN;

-- Recusa se ja houve pedido de mesa: conta, pagamento e auditoria apontam para ele.
DO $$
BEGIN
    IF to_regclass('mesa_pedido') IS NOT NULL AND EXISTS (SELECT 1 FROM mesa_pedido) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem pedidos de mesa.';
    END IF;
END $$;

DROP TABLE IF EXISTS mesa_aviso_gestor;
DROP TABLE IF EXISTS mesa_perda;
DROP TABLE IF EXISTS mesa_recado;
DROP TABLE IF EXISTS mesa_item_evento;
DROP TABLE IF EXISTS mesa_item_parte;
DROP TABLE IF EXISTS mesa_item;
DROP TABLE IF EXISTS mesa_pedido;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261207_01_mesas_pedidos';

COMMIT;
