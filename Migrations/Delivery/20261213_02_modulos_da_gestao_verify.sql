-- Verificação da 20261213_01_modulos_da_gestao: só leitura. Cada linha deve ter ok = true.
SELECT 'colunas dos ajustes' AS item, (SELECT count(*) FROM information_schema.columns
        WHERE table_schema = current_schema() AND table_name = 'estabelecimentos'
          AND column_name IN ('modulos_ligados_gestao', 'modulos_desligados_gestao')) = 2 AS ok
UNION ALL SELECT 'gatilho dos ajustes', EXISTS (SELECT 1 FROM pg_trigger
        WHERE tgname = 'trg_modulos_da_gestao' AND tgrelid = to_regclass('estabelecimentos'))
UNION ALL SELECT 'extra pago religa o modulo', EXISTS (SELECT 1 FROM pg_trigger
        WHERE tgname = 'trg_modulos_da_extra' AND tgrelid = to_regclass('compra_extra'))
UNION ALL SELECT 'versão registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261213_01_modulos_da_gestao');
