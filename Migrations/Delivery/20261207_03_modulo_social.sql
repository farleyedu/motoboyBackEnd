-- Modulo SOCIAL (Estudio e Editor do Zippy Social). O valor e criado SOZINHO neste arquivo: um valor de enum
-- recem-criado nao pode ser usado na mesma transacao. Nenhum pacote comercial ganha o modulo aqui.
-- Idempotente.
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'SOCIAL';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261207_03_modulo_social')
ON CONFLICT (version) DO NOTHING;
