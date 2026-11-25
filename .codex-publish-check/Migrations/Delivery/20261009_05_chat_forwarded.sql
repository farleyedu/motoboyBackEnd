BEGIN;
ALTER TABLE delivery_chat_message ADD COLUMN IF NOT EXISTS forwarded boolean NOT NULL DEFAULT false;
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261009_05_chat_forwarded') ON CONFLICT DO NOTHING;
COMMIT;
