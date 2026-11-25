BEGIN;

-- ---------------------------------------------------------------------------
-- Cada conversa passa a saber por QUAL numero de WhatsApp (canal_whatsapp) o cliente chegou. A resposta do bot e do
-- atendente sai sempre por esse mesmo numero, mesmo quando a loja tem mais de um. Conversas antigas ficam com NULL e
-- usam o primeiro numero em uso da loja. Aditivo e idempotente.
-- ---------------------------------------------------------------------------

ALTER TABLE conversas ADD COLUMN IF NOT EXISTS id_canal UUID NULL REFERENCES canal_whatsapp (id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS ix_conversas_canal ON conversas (id_canal) WHERE id_canal IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_11_conversas_canal')
ON CONFLICT (version) DO NOTHING;

COMMIT;
