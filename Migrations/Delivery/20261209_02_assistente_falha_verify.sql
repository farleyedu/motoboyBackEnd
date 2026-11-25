-- Verificacao da 20261209_01_assistente_falha: so leitura. Cada linha deve ter ok = true.
SELECT 'falha no estado da cadeia' AS item, EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_cadeia_estado' AND column_name = 'falha') AS ok
UNION ALL SELECT 'falha_em no estado da cadeia', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assistente_cadeia_estado' AND column_name = 'falha_em')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261209_01_assistente_falha');
