-- Verificacao da 20261203_03_mesas_nucleo: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela mesas_config' AS item, to_regclass('mesas_config') IS NOT NULL AS ok
UNION ALL SELECT 'tabela mesas_config_historico', to_regclass('mesas_config_historico') IS NOT NULL
UNION ALL SELECT 'historico imutavel', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesas_config_historico_imutavel')
UNION ALL SELECT 'coluna funcao_codigo', EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'gestao_pessoas' AND column_name = 'funcao_codigo')
UNION ALL SELECT 'funcao_codigo: bar', pg_get_constraintdef((SELECT oid FROM pg_constraint WHERE conname = 'ck_gestao_pessoas_funcao_codigo')) LIKE '%bar%'
UNION ALL SELECT 'tabela mesa_comando', to_regclass('mesa_comando') IS NOT NULL
UNION ALL SELECT 'comando: chave unica', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_mesa_comando_chave')
UNION ALL SELECT 'tabela mesa_evento', to_regclass('mesa_evento') IS NOT NULL
UNION ALL SELECT 'evento imutavel', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_evento_imutavel')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261203_03_mesas_nucleo');
