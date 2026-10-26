-- Conferencia da 20261101_50 (somente leitura). Cada linha deve sair com ok = true.
SELECT 'cobranca_chat.moeda' AS item, EXISTS (
    SELECT 1 FROM information_schema.columns WHERE table_name = 'cobranca_chat' AND column_name = 'moeda') AS ok
UNION ALL SELECT 'cobranca_chat.link_token_cifrado', EXISTS (
    SELECT 1 FROM information_schema.columns WHERE table_name = 'cobranca_chat' AND column_name = 'link_token_cifrado')
UNION ALL SELECT 'ck_cobranca_chat_status aceita aguardando_aprovacao', EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'ck_cobranca_chat_status' AND pg_get_constraintdef(oid) LIKE '%aguardando_aprovacao%')
UNION ALL SELECT 'ux_cobranca_chat_aberta_pedido_loja', to_regclass('ux_cobranca_chat_aberta_pedido_loja') IS NOT NULL
UNION ALL SELECT 'ux_cobranca_chat_aberta_pre_pedido_loja', to_regclass('ux_cobranca_chat_aberta_pre_pedido_loja') IS NOT NULL
UNION ALL SELECT 'cobranca_chat_config', to_regclass('cobranca_chat_config') IS NOT NULL
UNION ALL SELECT 'cobranca_chat_aprovacao', to_regclass('cobranca_chat_aprovacao') IS NOT NULL
UNION ALL SELECT 'ux_cobranca_chat_aprovacao_pendente', to_regclass('ux_cobranca_chat_aprovacao_pendente') IS NOT NULL
UNION ALL SELECT 'cobranca_chat_tarefa', to_regclass('cobranca_chat_tarefa') IS NOT NULL
UNION ALL SELECT 'ux_cobranca_chat_tarefa', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_cobranca_chat_tarefa');
