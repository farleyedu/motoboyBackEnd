-- Executar somente em ambiente de teste ou apos exportar os dados novos.
BEGIN;
DROP TRIGGER IF EXISTS delivery_chat_import ON delivery_motoboy_message;
DROP TRIGGER IF EXISTS delivery_chat_import ON motoboy_group_message;
DROP FUNCTION IF EXISTS delivery_chat_import_legacy();
DROP TABLE IF EXISTS delivery_chat_notification_read;
DROP TABLE IF EXISTS delivery_chat_push_outbox;
DROP TABLE IF EXISTS delivery_chat_push_subscription;
DROP TABLE IF EXISTS delivery_client_chat_dispatch;
DROP TABLE IF EXISTS delivery_chat_reaction;
DROP TABLE IF EXISTS delivery_chat_read;
DROP TABLE IF EXISTS delivery_chat_message;
DROP TABLE IF EXISTS delivery_chat_attachment;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261008_04_comunicacao';
COMMIT;
