-- Desfaz a Fase 5. Perde a configuracao de atendimento, as respostas rapidas e as mensagens ao motoboy.
BEGIN;
DROP INDEX IF EXISTS ix_pedido_conversa_id;
DROP TABLE IF EXISTS delivery_motoboy_message;
DROP TABLE IF EXISTS atendimento_respostas_rapidas;
DROP TABLE IF EXISTS estabelecimento_atendimento_config;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260927_03_atendimento';
COMMIT;
