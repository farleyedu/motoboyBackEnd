BEGIN;

CREATE TABLE IF NOT EXISTS delivery_chat_attachment (
    id UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    thread_key TEXT NOT NULL,
    owner_key TEXT NOT NULL,
    name TEXT NOT NULL,
    content_type TEXT NOT NULL,
    content BYTEA NOT NULL CHECK (octet_length(content) BETWEEN 1 AND 10485760),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS delivery_chat_message (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    thread_key TEXT NOT NULL,
    channel TEXT NOT NULL CHECK (channel IN ('store','group','private')),
    sender_key TEXT NOT NULL,
    sender_name TEXT NOT NULL,
    motoboy_id INTEGER NULL REFERENCES motoboy(id),
    recipient_id INTEGER NULL REFERENCES motoboy(id),
    pedido_id INTEGER NULL REFERENCES pedido(id),
    body TEXT NOT NULL CHECK (char_length(body) <= 500),
    client_id UUID NULL,
    fingerprint TEXT NULL,
    attachment_id UUID NULL REFERENCES delivery_chat_attachment(id),
    reply_to UUID NULL REFERENCES delivery_chat_message(id),
    mentions INTEGER[] NOT NULL DEFAULT '{}',
    legacy_id BIGINT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (channel, legacy_id),
    UNIQUE (estabelecimento_id, sender_key, client_id)
);
CREATE INDEX IF NOT EXISTS ix_delivery_chat_thread ON delivery_chat_message(estabelecimento_id,thread_key,sequence DESC);
CREATE TABLE IF NOT EXISTS delivery_chat_read (
    message_id UUID NOT NULL REFERENCES delivery_chat_message(id) ON DELETE CASCADE,
    actor_key TEXT NOT NULL,
    read_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY(message_id,actor_key)
);
CREATE TABLE IF NOT EXISTS delivery_chat_reaction (
    message_id UUID NOT NULL REFERENCES delivery_chat_message(id) ON DELETE CASCADE,
    actor_key TEXT NOT NULL,
    reaction TEXT NOT NULL CHECK (reaction IN ('like','heart','thanks','alert')),
    PRIMARY KEY(message_id,actor_key)
);
CREATE TABLE IF NOT EXISTS delivery_chat_notification_read (
    message_id UUID NOT NULL REFERENCES delivery_chat_message(id) ON DELETE CASCADE,
    actor_key TEXT NOT NULL,
    PRIMARY KEY(message_id,actor_key)
);
CREATE TABLE IF NOT EXISTS delivery_client_chat_dispatch (
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    actor_key TEXT NOT NULL,
    client_id UUID NOT NULL,
    pedido_id INTEGER NOT NULL REFERENCES pedido(id),
    fingerprint TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('sending','sent','uncertain')),
    message_id UUID NULL,
    attachment_id UUID NULL REFERENCES delivery_chat_attachment(id),
    reply_to UUID NULL,
    body TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY(estabelecimento_id,actor_key,client_id)
);
CREATE TABLE IF NOT EXISTS delivery_chat_push_subscription (
    token TEXT PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    session_id UUID NOT NULL,
    sound BOOLEAN NOT NULL DEFAULT TRUE,
    vibration BOOLEAN NOT NULL DEFAULT TRUE,
    muted BOOLEAN NOT NULL DEFAULT FALSE,
    mention_alerts BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS delivery_chat_push_outbox (
    id BIGSERIAL PRIMARY KEY,
    message_id UUID NOT NULL REFERENCES delivery_chat_message(id),
    token TEXT NOT NULL REFERENCES delivery_chat_push_subscription(token) ON DELETE CASCADE,
    mentioned BOOLEAN NOT NULL,
    state TEXT NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','sending','sent','failed','dismissed')),
    ticket_id TEXT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(message_id,token)
);

-- Os produtores antigos continuam escrevendo texto nas mesmas tabelas.
CREATE OR REPLACE FUNCTION delivery_chat_import_legacy() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'delivery_motoboy_message' THEN
        INSERT INTO delivery_chat_message(estabelecimento_id,thread_key,channel,sender_key,sender_name,motoboy_id,pedido_id,body,legacy_id,created_at_utc)
        VALUES(NEW.estabelecimento_id,'store:'||NEW.motoboy_id,'store',
            CASE WHEN NEW.direction='motoboy' THEN 'm:'||NEW.motoboy_id ELSE 'u:'||COALESCE(NEW.sent_by_user_id,0) END,
            CASE WHEN NEW.direction='motoboy' THEN COALESCE((SELECT nome FROM motoboy WHERE id=NEW.motoboy_id),'Motoboy') ELSE 'Loja' END,
            NEW.motoboy_id,NEW.pedido_id,NEW.body,NEW.id,NEW.created_at_utc) ON CONFLICT(channel,legacy_id) DO NOTHING;
    ELSE
        INSERT INTO delivery_chat_message(estabelecimento_id,thread_key,channel,sender_key,sender_name,motoboy_id,body,legacy_id,created_at_utc)
        VALUES(NEW.estabelecimento_id,'group','group',
            CASE WHEN NEW.sender_type='motoboy' THEN 'm:'||NEW.motoboy_id ELSE 'u:'||COALESCE(NEW.sent_by_user_id,0) END,
            CASE WHEN NEW.sender_type='motoboy' THEN COALESCE((SELECT nome FROM motoboy WHERE id=NEW.motoboy_id),'Motoboy') ELSE 'Loja' END,
            NEW.motoboy_id,NEW.body,NEW.id,NEW.created_at_utc) ON CONFLICT(channel,legacy_id) DO NOTHING;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS delivery_chat_import ON delivery_motoboy_message;
CREATE TRIGGER delivery_chat_import AFTER INSERT ON delivery_motoboy_message FOR EACH ROW EXECUTE FUNCTION delivery_chat_import_legacy();
DROP TRIGGER IF EXISTS delivery_chat_import ON motoboy_group_message;
CREATE TRIGGER delivery_chat_import AFTER INSERT ON motoboy_group_message FOR EACH ROW EXECUTE FUNCTION delivery_chat_import_legacy();

INSERT INTO delivery_chat_message(estabelecimento_id,thread_key,channel,sender_key,sender_name,motoboy_id,pedido_id,body,legacy_id,created_at_utc)
SELECT x.estabelecimento_id,'store:'||x.motoboy_id,'store',CASE WHEN x.direction='motoboy' THEN 'm:'||x.motoboy_id ELSE 'u:'||COALESCE(x.sent_by_user_id,0) END,
       CASE WHEN x.direction='motoboy' THEN COALESCE(m.nome,'Motoboy') ELSE 'Loja' END,x.motoboy_id,x.pedido_id,x.body,x.id,x.created_at_utc
FROM delivery_motoboy_message x LEFT JOIN motoboy m ON m.id=x.motoboy_id ORDER BY x.id
ON CONFLICT(channel,legacy_id) DO NOTHING;
INSERT INTO delivery_chat_message(estabelecimento_id,thread_key,channel,sender_key,sender_name,motoboy_id,body,legacy_id,created_at_utc)
SELECT x.estabelecimento_id,'group','group',CASE WHEN x.sender_type='motoboy' THEN 'm:'||x.motoboy_id ELSE 'u:'||COALESCE(x.sent_by_user_id,0) END,
       CASE WHEN x.sender_type='motoboy' THEN COALESCE(m.nome,'Motoboy') ELSE 'Loja' END,x.motoboy_id,x.body,x.id,x.created_at_utc
FROM motoboy_group_message x LEFT JOIN motoboy m ON m.id=x.motoboy_id ORDER BY x.id
ON CONFLICT(channel,legacy_id) DO NOTHING;

INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261008_04_comunicacao') ON CONFLICT DO NOTHING;
COMMIT;
