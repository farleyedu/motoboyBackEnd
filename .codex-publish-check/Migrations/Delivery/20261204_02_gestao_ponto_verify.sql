-- Verificacao da 20261204_01_gestao_ponto: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela gestao_ponto' AS item, to_regclass('gestao_ponto') IS NOT NULL AS ok
UNION ALL SELECT 'tabela gestao_ponto_pausa', to_regclass('gestao_ponto_pausa') IS NOT NULL
UNION ALL SELECT 'um ponto aberto por pessoa', to_regclass('ux_gestao_ponto_aberto') IS NOT NULL
UNION ALL SELECT 'uma pausa aberta por ponto', to_regclass('ux_gestao_ponto_pausa_aberta') IS NOT NULL
UNION ALL SELECT 'saida depois da entrada', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_gestao_ponto_saida')
UNION ALL SELECT 'modo', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_gestao_ponto_modo')
UNION ALL SELECT 'ponto sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'gestao_ponto_sem_delete')
UNION ALL SELECT 'pausa sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'gestao_ponto_pausa_sem_delete')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261204_01_gestao_ponto');
