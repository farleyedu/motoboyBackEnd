-- Verificacao das colunas de texto livre do pedido. Somente leitura; nao e aplicada automaticamente.

-- 1) Todas devem aparecer como 'text' (character_maximum_length NULL).
SELECT column_name, data_type, character_maximum_length
  FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'pedido'
   AND column_name IN ('nome_cliente', 'endereco_entrega', 'telefone_cliente', 'entrega_rua',
                        'entrega_bairro', 'entrega_cidade', 'region', 'observacoes', 'tipo_pagamento')
 ORDER BY column_name;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20260927_05_pedido_colunas_texto';
