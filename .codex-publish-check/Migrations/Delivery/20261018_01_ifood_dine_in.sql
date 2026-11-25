BEGIN;

-- Consumo no local (DINE_IN) deixa de ser recusado por regra fixa e passa a ser liberado pela tela de Gestao.
-- Comeca desligado: nenhuma loja ganha a operacao por esta migration.
ALTER TABLE ifood_app_settings ADD COLUMN IF NOT EXISTS dine_in_enabled BOOLEAN NOT NULL DEFAULT FALSE;

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261018_01_ifood_dine_in') ON CONFLICT DO NOTHING;
COMMIT;
