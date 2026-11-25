-- Desfaz 20261127_05_identidade_whatsapp.sql DE FORMA PARCIAL e nao destrutiva.
-- Remove so o indice auxiliar de usuario.telefone_e164. As tabelas usuario_whatsapp_vinculo e usuario_whatsapp_auditoria NAO sao apagadas:
-- guardam a prova de posse do numero de cada pessoa e a trilha de envio/confirmacao/revogacao, e nao ha como recria-las depois.
-- Para impedir o uso sem apagar, revogue os vinculos (veja rollback/README.md, secao "Identidade por WhatsApp").
BEGIN;
DROP INDEX IF EXISTS ix_usuario_telefone_e164;
COMMIT;
