-- Modalidade do pedido: retirada (entregue ao cliente na loja) ou delivery (despachado por motoboy).
-- Nao e o canal: um pedido do WhatsApp, do cardapio web ou do Caixa pode ser dos dois tipos. NULL = desconhecida: o sistema
-- nunca deduz delivery pela ausencia de motoboy nem retirada pela ausencia de endereco, so grava o que a origem afirmou.
-- Backfill apenas de fontes que afirmam a modalidade de verdade:
--   * venda de balcao do Caixa (origem_ref 'caixa:%', criada sem endereco): retirada;
--   * pedido convertido do cardapio web: tipo_entrega do pre-pedido (cardapio_pedido_publico.id_pedido);
--   * iFood: order_type do snapshot (DELIVERY = delivery, TAKEOUT = retirada).
-- O resto fica NULL ate alguem afirmar.

BEGIN;

ALTER TABLE pedido ADD COLUMN IF NOT EXISTS modalidade TEXT NULL;

ALTER TABLE pedido DROP CONSTRAINT IF EXISTS ck_pedido_modalidade;
ALTER TABLE pedido ADD CONSTRAINT ck_pedido_modalidade
    CHECK (modalidade IS NULL OR modalidade IN ('retirada', 'delivery'));

UPDATE pedido
   SET modalidade = 'retirada'
 WHERE modalidade IS NULL
   AND origem_ref LIKE 'caixa:%'
   AND entrega_rua = 'Balcão';

UPDATE pedido p
   SET modalidade = CASE WHEN lower(c.tipo_entrega) = 'entrega' THEN 'delivery' ELSE 'retirada' END
  FROM cardapio_pedido_publico c
 WHERE p.modalidade IS NULL
   AND c.id_pedido = p.id
   AND c.id_estabelecimento = p.id_estabelecimento
   AND lower(c.tipo_entrega) IN ('entrega', 'retirada');

DO $$
BEGIN
    IF to_regclass('ifood_order_links') IS NOT NULL AND to_regclass('ifood_order_snapshots') IS NOT NULL THEN
        UPDATE pedido p
           SET modalidade = CASE upper(s.order_type::text) WHEN 'DELIVERY' THEN 'delivery' ELSE 'retirada' END
          FROM ifood_order_links l
          JOIN ifood_order_snapshots s ON s.merchant_id = l.merchant_id AND s.external_order_id = l.external_order_id
                                      AND s.estabelecimento_id = l.estabelecimento_id
         WHERE p.modalidade IS NULL
           AND l.pedido_id = p.id AND l.estabelecimento_id = p.id_estabelecimento
           AND upper(s.order_type::text) IN ('DELIVERY', 'TAKEOUT');
    END IF;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261128_01_pedido_modalidade')
ON CONFLICT (version) DO NOTHING;

COMMIT;
