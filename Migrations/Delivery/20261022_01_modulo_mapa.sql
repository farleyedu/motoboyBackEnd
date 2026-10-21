-- Modulo MAPA (planos v2): a tela do mapa sem motoboy (plano Rota). Delivery passa a ser so o que e de motoboy
-- (app, despacho, rastreio ao vivo). O valor e criado SOZINHO neste arquivo: um valor de enum recem-criado nao pode
-- ser usado na mesma transacao (o backfill esta em 20261022_02_planos_v2.sql). Idempotente.
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'MAPA';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261022_01_modulo_mapa')
ON CONFLICT (version) DO NOTHING;
