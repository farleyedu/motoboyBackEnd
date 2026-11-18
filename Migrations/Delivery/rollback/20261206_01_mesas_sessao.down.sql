BEGIN;

-- Recusa se ja houve sessao de mesa: pedidos, pagamentos e auditoria apontam para ela.
DO $$
BEGIN
    IF to_regclass('mesa_sessao') IS NOT NULL AND EXISTS (SELECT 1 FROM mesa_sessao) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem sessoes de mesa.';
    END IF;
END $$;

DROP TABLE IF EXISTS mesa_anotacao;
DROP TABLE IF EXISTS mesa_cobertura;
DROP TABLE IF EXISTS mesa_solicitacao;
DROP TABLE IF EXISTS mesa_chamado;
DROP TABLE IF EXISTS mesa_comanda;
DROP TABLE IF EXISTS mesa_pessoa;
DROP TABLE IF EXISTS mesa_sessao_mesa;
DROP TABLE IF EXISTS mesa_sessao;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261206_01_mesas_sessao';

COMMIT;
