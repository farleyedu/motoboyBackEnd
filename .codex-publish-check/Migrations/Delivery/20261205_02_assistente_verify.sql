-- Verificacao da 20261205_01_assistente: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela assistente_conversas' AS item, to_regclass('assistente_conversas') IS NOT NULL AS ok
UNION ALL SELECT 'tabela assistente_mensagens', to_regclass('assistente_mensagens') IS NOT NULL
UNION ALL SELECT 'tabela assistente_acoes', to_regclass('assistente_acoes') IS NOT NULL
UNION ALL SELECT 'tabela assistente_auditoria', to_regclass('assistente_auditoria') IS NOT NULL
UNION ALL SELECT 'indice do limite diario', to_regclass('ix_assistente_mensagens_limite') IS NOT NULL
UNION ALL SELECT 'situacao da acao', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_assistente_acoes_situacao')
UNION ALL SELECT 'resultado da auditoria', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_assistente_auditoria_resultado')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_01_assistente');
