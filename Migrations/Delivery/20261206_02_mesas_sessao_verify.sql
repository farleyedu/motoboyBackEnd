-- Verificacao da 20261206_01_mesas_sessao: so leitura. Cada linha deve ter ok = true.
SELECT 'tabela mesa_sessao' AS item, to_regclass('mesa_sessao') IS NOT NULL AS ok
UNION ALL SELECT 'tabela mesa_sessao_mesa', to_regclass('mesa_sessao_mesa') IS NOT NULL
UNION ALL SELECT 'tabela mesa_pessoa', to_regclass('mesa_pessoa') IS NOT NULL
UNION ALL SELECT 'tabela mesa_comanda', to_regclass('mesa_comanda') IS NOT NULL
UNION ALL SELECT 'tabela mesa_chamado', to_regclass('mesa_chamado') IS NOT NULL
UNION ALL SELECT 'tabela mesa_solicitacao', to_regclass('mesa_solicitacao') IS NOT NULL
UNION ALL SELECT 'tabela mesa_cobertura', to_regclass('mesa_cobertura') IS NOT NULL
UNION ALL SELECT 'tabela mesa_anotacao', to_regclass('mesa_anotacao') IS NOT NULL
UNION ALL SELECT 'uma sessao aberta por mesa', to_regclass('ux_mesa_sessao_aberta') IS NOT NULL
UNION ALL SELECT 'qr unico', to_regclass('ux_mesa_sessao_qr') IS NOT NULL
UNION ALL SELECT 'mesa em uma juncao so', to_regclass('ux_mesa_sessao_mesa_ativa') IS NOT NULL
UNION ALL SELECT 'cadeira unica na sessao', to_regclass('ux_mesa_pessoa_cadeira') IS NOT NULL
UNION ALL SELECT 'uma coletiva por sessao', to_regclass('ux_mesa_comanda_coletiva') IS NOT NULL
UNION ALL SELECT 'estado da sessao', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_sessao_estado')
UNION ALL SELECT 'estado do chamado', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_mesa_chamado_estado')
UNION ALL SELECT 'sessao sem delete', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_sessao_sem_delete')
UNION ALL SELECT 'auditoria imutavel (P01)', EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesa_evento_imutavel')
UNION ALL SELECT 'versao registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261206_01_mesas_sessao');
