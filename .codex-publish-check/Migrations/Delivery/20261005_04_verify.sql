-- Verificacao dos avisos expandidos. Somente leitura; nao e aplicada automaticamente.

SELECT notify_received_enabled, notify_accepted_enabled, notify_arrived_enabled,
       notify_confirmacao_atendente_enabled, notify_pronto_retirada_enabled
  FROM delivery_settings
 LIMIT 5;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261005_03_avisos_pedido';
