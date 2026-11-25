DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='motoboy_active_sessions' AND column_name='paused_at_utc')
 OR to_regclass('motoboy_documentos') IS NULL OR to_regclass('delivery_completions') IS NULL OR to_regclass('delivery_completion_proofs') IS NULL
 OR NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='delivery_settings' AND column_name='require_delivery_proof')
 THEN RAISE EXCEPTION 'Migrações de conta/finalização incompletas'; END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version LIKE '20261008_%' ORDER BY version;
