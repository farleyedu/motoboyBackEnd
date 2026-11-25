-- Modulo MESAS (salao, app do garcom, cozinha e bar). O valor e criado SOZINHO neste arquivo: um valor de enum recem-criado
-- nao pode ser usado na mesma transacao. Nenhum pacote comercial ganha o modulo aqui (preco a decidir); a Gestao liga por loja.
-- Idempotente.
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'MESAS';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261203_01_modulo_mesas')
ON CONFLICT (version) DO NOTHING;
