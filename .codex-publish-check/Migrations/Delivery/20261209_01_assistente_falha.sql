BEGIN;

-- Assistente: guarda a ultima falha explicada de cada item da cadeia (CONTRATO-API §12.6: codigo, etapa, status, mensagem limpa
-- do provedor e acoes). Sem esta coluna o backend segue funcionando e so nao mostra a ultima falha. Requer 20261205_07.
-- Aditiva e idempotente.

ALTER TABLE assistente_cadeia_estado ADD COLUMN IF NOT EXISTS falha JSONB NULL;
ALTER TABLE assistente_cadeia_estado ADD COLUMN IF NOT EXISTS falha_em TIMESTAMPTZ NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261209_01_assistente_falha')
ON CONFLICT (version) DO NOTHING;

COMMIT;
