-- Verificacao da 20261205_05_assistente_uso: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela assistente_uso' AS item, to_regclass('assistente_uso') IS NOT NULL AS ok
UNION ALL SELECT 'tabela assistente_eventos', to_regclass('assistente_eventos') IS NOT NULL
UNION ALL SELECT 'um bloqueio por loja por dia', to_regclass('ux_assistente_eventos_limite_dia') IS NOT NULL
UNION ALL SELECT 'precos na config', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_config' AND column_name = 'precos')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_05_assistente_uso');
