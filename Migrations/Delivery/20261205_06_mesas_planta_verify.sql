-- Verificacao da 20261205_05_mesas_planta: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela mesa_planta' AS item, to_regclass('mesa_planta') IS NOT NULL AS ok
UNION ALL SELECT 'tabela mesa', to_regclass('mesa') IS NOT NULL
UNION ALL SELECT 'tabela mesa_estado', to_regclass('mesa_estado') IS NOT NULL
UNION ALL SELECT 'tabela mesa_andar', to_regclass('mesa_andar') IS NOT NULL
UNION ALL SELECT 'tabela mesa_area', to_regclass('mesa_area') IS NOT NULL
UNION ALL SELECT 'um rascunho por loja', to_regclass('ux_mesa_planta_rascunho') IS NOT NULL
UNION ALL SELECT 'uma publicada por loja', to_regclass('ux_mesa_planta_publicada') IS NOT NULL
UNION ALL SELECT 'numero unico entre ativas', to_regclass('ux_mesa_numero_ativa') IS NOT NULL
UNION ALL SELECT 'chave da planta estavel', to_regclass('ux_mesa_chave_planta') IS NOT NULL
UNION ALL SELECT 'status da mesa', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_estado_status')
UNION ALL SELECT 'sessao so com mesa ocupada', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_estado_sessao')
UNION ALL SELECT 'mesa sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_sem_delete')
UNION ALL SELECT 'planta sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_planta_sem_delete')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261205_05_mesas_planta');
