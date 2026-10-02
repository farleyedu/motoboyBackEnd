-- Verificacao da confirmacao do cardapio web pelo WhatsApp. Somente leitura; nao e aplicada automaticamente.

SELECT column_name
  FROM information_schema.columns
 WHERE table_name = 'cardapio_pedido_publico'
   AND column_name IN ('codigo_confirmacao', 'codigo_expira_em', 'canal_confirmacao', 'telefone_contato',
                       'id_conversa', 'confirmado_em', 'aceito_em', 'recusado_em', 'motivo_recusa', 'id_pedido')
 ORDER BY column_name;

SELECT indexname FROM pg_indexes
 WHERE tablename = 'cardapio_pedido_publico'
   AND indexname IN ('ux_cardapio_pedido_publico_codigo_ativo', 'ix_cardapio_pedido_publico_aguardando');

SELECT status, COUNT(*) AS total FROM cardapio_pedido_publico GROUP BY status ORDER BY status;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261002_03_cardapio_confirmacao_whatsapp';
