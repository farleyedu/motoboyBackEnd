BEGIN;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM delivery_rider_work_entries WHERE period_total_seconds IS NOT NULL OR backfilled_by IS NOT NULL) THEN
  RAISE EXCEPTION 'Rollback bloqueado: existem períodos parciais ou lançamentos retroativos registrados. Preserve os registros.';
 END IF;
END $$;
ALTER TABLE delivery_rider_work_entries DROP CONSTRAINT IF EXISTS delivery_rider_work_entries_period_check;
ALTER TABLE delivery_rider_work_entries DROP COLUMN IF EXISTS period_total_seconds;
ALTER TABLE delivery_rider_work_entries DROP COLUMN IF EXISTS period_worked_seconds;
ALTER TABLE delivery_rider_work_entries DROP COLUMN IF EXISTS backfilled_by;
COMMIT;
