-- Verificação da 20261212_01_turno_entrega: só leitura. Cada linha deve ter ok = true.
SELECT 'tabela dos turnos' AS item, to_regclass('delivery_turno_entrega') IS NOT NULL AS ok
UNION ALL SELECT 'tabela do estado', to_regclass('delivery_turno_estado') IS NOT NULL
UNION ALL SELECT 'tabela dos créditos', to_regclass('delivery_turno_credito') IS NOT NULL
UNION ALL SELECT 'um turno por início', to_regclass('ux_delivery_turno_entrega_inicio') IS NOT NULL
UNION ALL SELECT 'um crédito por motoboy no turno', to_regclass('ux_delivery_turno_credito_turno') IS NOT NULL
UNION ALL SELECT 'um crédito pendente por motoboy', to_regclass('ux_delivery_turno_credito_pendente') IS NOT NULL
UNION ALL SELECT 'versão registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261212_01_turno_entrega');
