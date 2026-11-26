-- Verificação da 20261212_03_loja_interesse: só leitura. Cada linha deve ter ok = true.
SELECT 'tabela do interesse' AS item, to_regclass('loja_interesse') IS NOT NULL AS ok
UNION ALL SELECT 'sem repetir a mesma chave', to_regclass('ux_loja_interesse_chave') IS NOT NULL
UNION ALL SELECT 'versão registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261212_03_loja_interesse');
