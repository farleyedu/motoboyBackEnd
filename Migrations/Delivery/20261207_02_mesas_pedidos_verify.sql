-- Verificacao da 20261207_01_mesas_pedidos: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela mesa_pedido' AS item, to_regclass('mesa_pedido') IS NOT NULL AS ok
UNION ALL SELECT 'tabela mesa_item', to_regclass('mesa_item') IS NOT NULL
UNION ALL SELECT 'tabela mesa_item_parte', to_regclass('mesa_item_parte') IS NOT NULL
UNION ALL SELECT 'tabela mesa_item_evento', to_regclass('mesa_item_evento') IS NOT NULL
UNION ALL SELECT 'tabela mesa_recado', to_regclass('mesa_recado') IS NOT NULL
UNION ALL SELECT 'tabela mesa_perda', to_regclass('mesa_perda') IS NOT NULL
UNION ALL SELECT 'tabela mesa_aviso_gestor', to_regclass('mesa_aviso_gestor') IS NOT NULL
UNION ALL SELECT 'fila por estacao', to_regclass('ix_mesa_item_estacao') IS NOT NULL
UNION ALL SELECT 'estado do pedido', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_pedido_estado')
UNION ALL SELECT 'estado do item', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_item_estado')
UNION ALL SELECT 'quem leva', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_item_quem_leva')
UNION ALL SELECT 'historico do item imutavel', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_item_evento_imutavel')
UNION ALL SELECT 'item sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_item_sem_delete')
UNION ALL SELECT 'sessao (P07) existe', to_regclass('mesa_sessao') IS NOT NULL
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261207_01_mesas_pedidos');
