-- Verificacao do simulador v2. Somente leitura; nao e aplicada automaticamente.
SELECT to_regclass('simulador_evento') AS eventos, to_regclass('simulador_sessao') AS sessoes;

SELECT column_name FROM information_schema.columns
 WHERE table_schema = current_schema()
   AND ((table_name = 'pedido' AND column_name IN ('confirmado_em_utc', 'preparo_em_utc', 'canal'))
     OR (table_name = 'clientes' AND column_name IN ('avatar', 'cpf', 'data_nascimento', 'referencia',
                                                     'canal_preferido', 'origem', 'tags', 'consentimento_whatsapp')))
 ORDER BY table_name, column_name;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20260930_01_simulador_v2';
