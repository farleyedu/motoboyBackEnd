-- Verificacao da 20261205_07_assistente_cadeia: so leitura. Cada linha deve ter ok = true.
SELECT 'cadeia na config' AS item, EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_config' AND column_name = 'cadeia') AS ok
UNION ALL SELECT 'provedores na config', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_config' AND column_name = 'provedores')
UNION ALL SELECT 'tabela assistente_cadeia_estado', to_regclass('assistente_cadeia_estado') IS NOT NULL
UNION ALL SELECT 'item no uso', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_uso' AND column_name = 'item_id')
UNION ALL SELECT 'evento pulo aceito', pg_get_constraintdef((SELECT oid FROM pg_constraint WHERE conname = 'ck_assistente_eventos_tipo')) LIKE '%pulo%'
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_07_assistente_cadeia');
