BEGIN;
CREATE TABLE IF NOT EXISTS delivery_web_push_subscription (
 token TEXT PRIMARY KEY REFERENCES delivery_chat_push_subscription(token) ON DELETE CASCADE,
 endpoint TEXT NOT NULL UNIQUE,
 p256dh TEXT NOT NULL,
 auth TEXT NOT NULL
);
INSERT INTO delivery_tracking_schema_versions(version)
 VALUES ('20261011_02_oferta_web_push') ON CONFLICT DO NOTHING;
COMMIT;
