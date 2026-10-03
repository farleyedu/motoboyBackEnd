BEGIN;

-- Reverte o catalogo de servicos e os canais de WhatsApp. waba_phone nunca foi alterada, entao o codigo antigo continua
-- funcionando. ATENCAO: apaga os canais criados depois da migration (numeros novos, modos, tokens cifrados).

DROP TABLE IF EXISTS canal_whatsapp_migracao_pendencia;
DROP TABLE IF EXISTS canal_whatsapp_auditoria;
DROP TABLE IF EXISTS canal_servico;
DROP TABLE IF EXISTS canal_whatsapp;
DROP TABLE IF EXISTS estabelecimento_servico;
DROP TABLE IF EXISTS tipo_servico;
DROP TABLE IF EXISTS servico_catalogo;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_07_catalogo_servicos_canais';

COMMIT;
