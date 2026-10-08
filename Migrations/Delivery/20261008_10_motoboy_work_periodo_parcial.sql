BEGIN;
ALTER TABLE delivery_rider_work_entries
 ADD COLUMN IF NOT EXISTS period_total_seconds bigint,
 ADD COLUMN IF NOT EXISTS period_worked_seconds bigint,
 ADD COLUMN IF NOT EXISTS backfilled_by integer;
ALTER TABLE delivery_rider_work_entries DROP CONSTRAINT IF EXISTS delivery_rider_work_entries_period_check;
ALTER TABLE delivery_rider_work_entries ADD CONSTRAINT delivery_rider_work_entries_period_check
 CHECK (period_worked_seconds IS NULL OR (period_total_seconds IS NOT NULL AND period_worked_seconds>0 AND period_worked_seconds<=period_total_seconds));
COMMIT;
