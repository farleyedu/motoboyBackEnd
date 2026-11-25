-- Verificacao dos textos do atendimento. Somente leitura; nao e aplicada automaticamente.

SELECT estabelecimento_id, jsonb_object_keys(mensagens) AS texto_configurado
  FROM estabelecimento_atendimento_config
 WHERE mensagens <> '{}'::jsonb
 ORDER BY estabelecimento_id;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261002_15_atendimento_mensagens';
