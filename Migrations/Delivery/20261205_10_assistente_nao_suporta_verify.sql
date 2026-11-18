-- Verificacao da 20261205_09_assistente_nao_suporta: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela assistente_provedor_nao_suporta' AS item, to_regclass('assistente_provedor_nao_suporta') IS NOT NULL AS ok
UNION ALL SELECT 'indice do alerta cadeia esgotada', to_regclass('ix_assistente_eventos_cadeia_esgotada') IS NOT NULL
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_09_assistente_nao_suporta');
