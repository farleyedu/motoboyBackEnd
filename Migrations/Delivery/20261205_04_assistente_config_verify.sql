-- Verificacao da 20261205_03_assistente_config: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela assistente_config' AS item, to_regclass('assistente_config') IS NOT NULL AS ok
UNION ALL SELECT 'tabela assistente_config_historico', to_regclass('assistente_config_historico') IS NOT NULL
UNION ALL SELECT 'uma linha so', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_assistente_config_linha')
UNION ALL SELECT 'provedor por mensagem', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_mensagens' AND column_name = 'provedor')
UNION ALL SELECT 'reserva por mensagem', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_mensagens' AND column_name = 'usou_reserva')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_03_assistente_config');
