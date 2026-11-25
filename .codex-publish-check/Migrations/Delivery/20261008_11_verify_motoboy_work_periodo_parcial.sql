DO $$
BEGIN
 IF (SELECT COUNT(*) FROM information_schema.columns
      WHERE table_name='delivery_rider_work_entries'
        AND column_name IN ('period_total_seconds','period_worked_seconds','backfilled_by')) <> 3 THEN
  RAISE EXCEPTION 'Migration de período parcial e lançamento retroativo incompleta';
 END IF;
 IF EXISTS(SELECT 1 FROM delivery_rider_work_entries
            WHERE period_worked_seconds IS NOT NULL
              AND (period_total_seconds IS NULL OR period_worked_seconds<=0 OR period_worked_seconds>period_total_seconds)) THEN
  RAISE EXCEPTION 'Lançamento de período com segundos inconsistentes';
 END IF;
END $$;
