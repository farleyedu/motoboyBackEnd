-- ATENCAO: destrutivo. Apaga todo o schema novo do delivery tracking V2 e
-- os dados que ele acumulou (sessoes, amostras de localizacao, eventos,
-- outbox). Faca backup/export antes se precisar preservar historico.
--
-- Assume que motoboy.id_usuario e motoboy.id_estabelecimento sao colunas
-- LEGADAS pre-existentes (o "ADD COLUMN IF NOT EXISTS" da 02_schema e apenas
-- defensivo) — por isso nao sao removidas aqui. Confirme isso no seu
-- ambiente antes de rodar (ver rollback/README.md).

BEGIN;

DROP TABLE IF EXISTS delivery_realtime_outbox;
DROP TABLE IF EXISTS motoboy_location_current;
DROP TABLE IF EXISTS motoboy_location_samples;
DROP TABLE IF EXISTS motoboy_operational_session_events;

ALTER TABLE motoboy_active_sessions
    DROP COLUMN IF EXISTS version,
    DROP COLUMN IF EXISTS end_reason,
    DROP COLUMN IF EXISTS ended_at_utc,
    DROP COLUMN IF EXISTS expires_at_utc,
    DROP COLUMN IF EXISTS last_heartbeat_at_utc,
    DROP COLUMN IF EXISTS started_at_utc,
    DROP COLUMN IF EXISTS idempotency_key,
    DROP COLUMN IF EXISTS started_by_user_id,
    DROP COLUMN IF EXISTS client_instance_id,
    DROP COLUMN IF EXISTS origin,
    DROP COLUMN IF EXISTS contract_version,
    DROP COLUMN IF EXISTS session_epoch;

DROP TABLE IF EXISTS motoboy_estabelecimento;

DROP INDEX IF EXISTS ix_pedido_id_estabelecimento;
ALTER TABLE pedido DROP COLUMN IF EXISTS id_estabelecimento;

DROP INDEX IF EXISTS ix_motoboy_location_history_daily_lookup;
DROP TABLE IF EXISTS motoboy_location_history_daily;

DROP INDEX IF EXISTS ix_motoboy_location_state_estabelecimento;
DROP TABLE IF EXISTS motoboy_location_state;

ALTER TABLE motoboy
    DROP COLUMN IF EXISTS is_simulated,
    DROP COLUMN IF EXISTS canonical_motoboy_id;
-- id_usuario e id_estabelecimento NAO sao removidas: presumidas legadas (ver header).

DROP SEQUENCE IF EXISTS motoboy_session_epoch_seq;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20260722_02_schema';
-- delivery_tracking_schema_versions em si e mantida (tabela de controle de migracao).

COMMIT;
