BEGIN;

-- ---------------------------------------------------------------------------
-- Modulo CLIENTES (cadastro de clientes do estabelecimento).
-- estabelecimentos.modulos_ativos e um array do enum modulo_enum; gravar um valor que o
-- enum nao conhece derruba o salvamento do estabelecimento. Por isso o valor e criado aqui,
-- SOZINHO neste arquivo: um valor de enum recem-criado nao pode ser usado na mesma
-- transacao (o backfill que o usa esta em 20260929_03_modulo_clientes_backfill.sql).
-- Requer PostgreSQL 12+ (ADD VALUE dentro de transacao). Idempotente.
-- A API funciona sem este valor: o salvamento de estabelecimento ignora modulos que o
-- enum ainda nao conhece, entao a ordem entre deploy e migration nao quebra nada.
-- ---------------------------------------------------------------------------

ALTER TYPE modulo_enum ADD VALUE IF NOT EXISTS 'CLIENTES';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260929_01_modulo_clientes')
ON CONFLICT (version) DO NOTHING;

COMMIT;
