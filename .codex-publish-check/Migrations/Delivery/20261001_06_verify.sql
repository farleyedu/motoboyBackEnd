-- Verificacao das colunas de texto livre do pedido (continuacao). Somente leitura; nao e aplicada automaticamente.

-- 1) Todas devem aparecer como 'text' (character_maximum_length NULL).
SELECT column_name, data_type, character_maximum_length
  FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'pedido'
   AND column_name IN ('entrega_numero', 'entrega_estado', 'entrega_cep', 'status_pagamento', 'codigo_entrega')
 ORDER BY column_name;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261001_05_pedido_colunas_texto_2';
