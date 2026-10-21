-- Modulos novos (plano de modulos, secao 1.1): cada tela vira um modulo ligavel por estabelecimento.
-- Os valores sao criados SOZINHOS neste arquivo: um valor de enum recem-criado nao pode ser usado na mesma transacao
-- (o backfill que os usa esta em 20261020_04_modulos_backfill.sql). Requer PostgreSQL 12+. Idempotente.
-- O CREATE TYPE original de modulo_enum nao esta no repositorio; por isso tambem garantimos os valores ja usados.

ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'GERAL';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'PEDIDOS';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'DELIVERY';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'CLIENTES';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'WHATSAPP';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'CARDAPIO';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'CARDAPIOWEB';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'INTELIGENCIA';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'SERVICOS';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'FAQ';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'DISPONIBILIDADE';

-- Novos.
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'IFOOD';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'IMPRESSAO';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'REMUNERACAO';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'SIMULADOR';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'EXPERIENCE';
ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'FINANCEIRO';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261020_03_modulos_novos')
ON CONFLICT (version) DO NOTHING;
