BEGIN;
ALTER TABLE delivery_chat_reaction DROP CONSTRAINT IF EXISTS delivery_chat_reaction_reaction_check;
ALTER TABLE delivery_client_chat_reaction DROP CONSTRAINT IF EXISTS delivery_client_chat_reaction_reaction_check;
ALTER TABLE delivery_chat_reaction ADD CONSTRAINT delivery_chat_reaction_reaction_check CHECK (length(reaction) BETWEEN 1 AND 32);
ALTER TABLE delivery_client_chat_reaction ADD CONSTRAINT delivery_client_chat_reaction_reaction_check CHECK (length(reaction) BETWEEN 1 AND 32);
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261009_02_chat_emoji_reactions') ON CONFLICT DO NOTHING;
COMMIT;
