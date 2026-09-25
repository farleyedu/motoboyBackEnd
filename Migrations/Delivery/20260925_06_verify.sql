-- Verificacao da Fase 2 (nucleo de pedido). Somente leitura; nao e aplicada automaticamente.

-- 1) Enum de modulos conhece PEDIDOS (deve listar 1 linha).
SELECT enumlabel
  FROM pg_enum
 WHERE enumtypid = 'modulo_enum'::regtype
   AND enumlabel = 'PEDIDOS';

-- 2) Colunas novas em pedido (deve listar 7 linhas).
SELECT column_name, data_type, is_nullable, column_default
  FROM information_schema.columns
 WHERE table_name = 'pedido'
   AND column_name IN ('origem', 'origem_ref', 'conversa_id', 'cliente_id', 'subtotal', 'taxa_entrega', 'desconto')
 ORDER BY column_name;

-- 3) Pedido com origem fora da lista (nunca deveria acontecer: ha CHECK).
SELECT id, origem
  FROM pedido
 WHERE origem NOT IN ('atendente', 'cardapio_web', 'ifood', 'ia_whatsapp', 'simulador');

-- 4) Pedidos do iFood sem origem 'ifood' (o backfill deveria ter corrigido).
SELECT id, id_ifood, origem, origem_ref
  FROM pedido
 WHERE id_ifood IS NOT NULL
   AND (origem <> 'ifood' OR origem_ref IS DISTINCT FROM id_ifood);

-- 5) Origem/referencia repetida (o indice unico deveria ter impedido).
SELECT id_estabelecimento, origem, origem_ref, COUNT(*) AS total
  FROM pedido
 WHERE origem_ref IS NOT NULL
 GROUP BY id_estabelecimento, origem, origem_ref
HAVING COUNT(*) > 1;

-- 6) Itens sem pedido (a FK com CASCADE deveria ter impedido).
SELECT i.id, i.pedido_id
  FROM pedido_item i
  LEFT JOIN pedido p ON p.id = i.pedido_id
 WHERE p.id IS NULL;

-- 7) Rascunho (status 6) nunca deve ter motoboy nem parada ativa.
SELECT p.id, p.motoboy_responsavel
  FROM pedido p
 WHERE p.status_pedido = 6
   AND (p.motoboy_responsavel IS NOT NULL
        OR EXISTS (SELECT 1 FROM delivery_route_stops s
                    WHERE s.pedido_id = p.id AND s.stop_status IN ('assigned', 'en_route')));

-- 8) Estabelecimentos com DELIVERY ou CARDAPIOWEB sem PEDIDOS (o backfill deveria ter corrigido).
SELECT id, nome_fantasia
  FROM estabelecimentos
 WHERE (modulos_ativos @> ARRAY['DELIVERY']::modulo_enum[] OR modulos_ativos @> ARRAY['CARDAPIOWEB']::modulo_enum[])
   AND NOT (modulos_ativos @> ARRAY['PEDIDOS']::modulo_enum[]);

SELECT version, applied_at_utc
  FROM delivery_tracking_schema_versions
 WHERE version LIKE '20260925%'
 ORDER BY applied_at_utc, version;
