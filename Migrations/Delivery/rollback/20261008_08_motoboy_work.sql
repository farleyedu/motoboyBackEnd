BEGIN;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM delivery_rider_pay_plans) OR EXISTS(SELECT 1 FROM delivery_rider_quotes)
 OR EXISTS(SELECT 1 FROM delivery_rider_work_entries) OR EXISTS(SELECT 1 FROM delivery_rider_settlements)
 OR EXISTS(SELECT 1 FROM delivery_rider_support) THEN
 RAISE EXCEPTION 'Rollback bloqueado: existem regras, ofertas, ganhos, acertos ou solicitações. Preserve os registros e faça correção progressiva.';
 END IF;
END $$;
DROP TABLE delivery_rider_support;
DROP TABLE delivery_rider_settlement_events;
DROP TABLE delivery_rider_work_entries;
DROP TABLE delivery_rider_settlements;
DROP TABLE delivery_rider_quotes;
DROP TABLE delivery_rider_pay_plans;
DROP INDEX IF EXISTS ix_rider_work_history;
COMMIT;
