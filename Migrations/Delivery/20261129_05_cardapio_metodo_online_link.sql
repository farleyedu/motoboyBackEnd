BEGIN;

-- Pedido pago por link hospedado (InfinitePay) nao e Pix nem cartao ate o servidor verificar o capture_method: ganha o valor 'online'.
-- Aditivo: so alarga a regra; nenhuma linha existente muda.
ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_metodo_online;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_metodo_online CHECK (
        (pagamento_online AND metodo_online IN ('pix', 'cartao', 'online')) OR (NOT pagamento_online AND metodo_online IS NULL));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261129_05_cardapio_metodo_online_link')
ON CONFLICT (version) DO NOTHING;

COMMIT;
