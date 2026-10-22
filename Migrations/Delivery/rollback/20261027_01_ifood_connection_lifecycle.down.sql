BEGIN;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM ifood_connections GROUP BY merchant_id HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION 'Ha merchants com historico em mais de uma loja; o modelo antigo (merchant_id unico) nao os comporta. Rollback recusado.';
    END IF;
END $$;
DROP INDEX IF EXISTS ux_ifood_connections_active_store;
DROP INDEX IF EXISTS ux_ifood_connections_active_merchant;
ALTER TABLE ifood_connections DROP CONSTRAINT IF EXISTS ifood_connections_pkey;
ALTER TABLE ifood_connections ADD CONSTRAINT ifood_connections_pkey PRIMARY KEY (merchant_id);
ALTER TABLE ifood_connections DROP CONSTRAINT IF EXISTS ck_ifood_connections_poll_state;
ALTER TABLE ifood_connections DROP COLUMN IF EXISTS poll_state, DROP COLUMN IF EXISTS poll_error_code,
    DROP COLUMN IF EXISTS poll_failures, DROP COLUMN IF EXISTS poll_blocked_until_utc, DROP COLUMN IF EXISTS last_poll_at_utc,
    DROP COLUMN IF EXISTS last_poll_ok_at_utc, DROP COLUMN IF EXISTS remote_check_code, DROP COLUMN IF EXISTS remote_checked_at_utc;
DROP TABLE IF EXISTS ifood_connection_audit;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261027_01_ifood_connection_lifecycle';
COMMIT;
