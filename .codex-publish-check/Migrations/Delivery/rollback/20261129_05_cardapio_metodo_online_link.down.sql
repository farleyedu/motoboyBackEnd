BEGIN;

-- Nao reescreve historico: bloqueia se algum pedido ja foi criado para pagar por link.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM cardapio_pedido_publico WHERE metodo_online = 'online') THEN
        RAISE EXCEPTION 'Rollback bloqueado: existem pedidos com pagamento por link (metodo_online = online)';
    END IF;
END $$;

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_metodo_online;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_metodo_online CHECK (
        (pagamento_online AND metodo_online IN ('pix', 'cartao')) OR (NOT pagamento_online AND metodo_online IS NULL));
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261129_05_cardapio_metodo_online_link';

COMMIT;
