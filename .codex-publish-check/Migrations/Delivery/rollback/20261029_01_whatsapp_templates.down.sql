-- Remove so os modelos do Zippy; os modelos continuam existindo na Meta (WhatsApp Manager).
BEGIN;
DROP TABLE IF EXISTS whatsapp_template_evento;
DROP TABLE IF EXISTS whatsapp_template;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261029_01_whatsapp_templates';
COMMIT;
