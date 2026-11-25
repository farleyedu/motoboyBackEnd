BEGIN;
-- Apaga o cadastro do app ZippyGo (App ID, Configuration ID e App Secret cifrado): a conexao pela Meta fica indisponivel ate cadastrar de novo.
DROP TABLE IF EXISTS whatsapp_meta_app;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261030_03_whatsapp_meta_app';
COMMIT;
