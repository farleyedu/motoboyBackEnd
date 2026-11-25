-- Conferencia de 20261101_30 (somente leitura). Cada linha deve sair com ok = true.
SELECT 'colunas do pre-pedido' AS item,
       count(*) = 8 AS ok
  FROM information_schema.columns
 WHERE table_name = 'cardapio_pedido_publico'
   AND column_name IN ('pagamento_online', 'metodo_online', 'chave_idempotencia', 'id_intent_atual', 'id_intent_pago',
                       'pago_em', 'cancelado_em', 'motivo_cancelamento')
UNION ALL
SELECT 'status aguardando_pagamento/aceitando/cancelado',
       pg_get_constraintdef(oid) LIKE '%aguardando_pagamento%' AND pg_get_constraintdef(oid) LIKE '%aceitando%'
       AND pg_get_constraintdef(oid) LIKE '%cancelado%'
  FROM pg_constraint WHERE conname = 'ck_cardapio_pedido_publico_status'
UNION ALL
SELECT 'tabela de reembolso', to_regclass('cardapio_pedido_reembolso') IS NOT NULL
UNION ALL
SELECT 'chave unica do reembolso', to_regclass('ux_cardapio_pedido_reembolso_chave') IS NOT NULL
UNION ALL
SELECT 'chave unica do checkout', to_regclass('ux_cardapio_pedido_publico_chave') IS NOT NULL
UNION ALL
SELECT 'pedido online sem metodo', NOT EXISTS (
       SELECT 1 FROM cardapio_pedido_publico WHERE pagamento_online AND metodo_online IS NULL)
UNION ALL
SELECT 'pedido online aceito sem pagamento', NOT EXISTS (
       SELECT 1 FROM cardapio_pedido_publico
        WHERE pagamento_online AND status IN ('aguardando_aceite', 'aceitando', 'aceito') AND id_intent_pago IS NULL)
UNION ALL
SELECT 'reembolso acima do pago', NOT EXISTS (
       SELECT 1 FROM cardapio_pedido_reembolso r JOIN pagamento_intent i ON i.id = r.id_intent
        WHERE r.status NOT IN ('negado', 'falhou')
        GROUP BY i.id, i.valor_pago HAVING sum(r.valor) > i.valor_pago);
