BEGIN;
ALTER TABLE delivery_chat_push_outbox ADD COLUMN IF NOT EXISTS recipient_estabelecimento_id UUID;
ALTER TABLE delivery_chat_push_outbox ADD COLUMN IF NOT EXISTS recipient_motoboy_id INTEGER;
ALTER TABLE delivery_chat_push_outbox ADD COLUMN IF NOT EXISTS recipient_session_id UUID;
-- Avisos antigos nao possuem identidade imutavel: nao inferir pelo dono atual do token.
UPDATE delivery_chat_push_outbox SET state='dismissed'
WHERE state IN ('queued','sending') AND recipient_session_id IS NULL;
CREATE TABLE IF NOT EXISTS delivery_client_chat_reaction (
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    message_id UUID NOT NULL,
    actor_key TEXT NOT NULL,
    reaction TEXT NOT NULL CHECK(reaction IN ('like','heart','thanks','alert')),
    PRIMARY KEY(estabelecimento_id,message_id,actor_key)
);
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261008_06_comunicacao_correcoes') ON CONFLICT DO NOTHING;
COMMIT;
