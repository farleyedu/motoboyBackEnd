-- Verificacao da 20261205_11_assistente_chaves: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela assistente_chaves' AS item, to_regclass('assistente_chaves') IS NOT NULL AS ok
UNION ALL SELECT 'tabela assistente_chaves_auditoria', to_regclass('assistente_chaves_auditoria') IS NOT NULL
UNION ALL SELECT 'so aceita cifrada', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_assistente_chaves_cifrada')
UNION ALL SELECT 'nenhuma chave em texto puro', NOT EXISTS (SELECT 1 FROM assistente_chaves WHERE cifrada NOT LIKE 'ak1.%')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_11_assistente_chaves');
