-- Verificacao do backfill de compartilhamento de localizacao dos motoboys simulados. Somente leitura.

SELECT count(*) AS simulados, count(*) FILTER (WHERE compartilhar_localizacao_cliente) AS compartilhando
  FROM motoboy
 WHERE is_simulated = TRUE;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261005_05_simulador_compartilha_localizacao';
