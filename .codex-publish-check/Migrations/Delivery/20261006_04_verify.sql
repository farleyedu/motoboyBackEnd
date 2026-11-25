-- Verificacao do grupo de motoboys por estabelecimento. Somente leitura; nao e aplicada automaticamente.

SELECT estabelecimento_id, count(*) AS mensagens,
       count(*) FILTER (WHERE sender_type = 'motoboy') AS de_motoboys,
       count(*) FILTER (WHERE sender_type = 'operator') AS de_atendente
  FROM motoboy_group_message
 GROUP BY estabelecimento_id;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261006_03_motoboy_grupo';
