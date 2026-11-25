DO $$
BEGIN
 IF to_regclass('delivery_rider_pay_plans') IS NULL OR to_regclass('delivery_rider_quotes') IS NULL
 OR to_regclass('delivery_rider_work_entries') IS NULL OR to_regclass('delivery_rider_settlements') IS NULL
 OR to_regclass('delivery_rider_settlement_events') IS NULL OR to_regclass('delivery_rider_support') IS NULL THEN
 RAISE EXCEPTION 'Migration de trabalho do motoboy incompleta';
 END IF;
 IF EXISTS(SELECT 1 FROM delivery_rider_work_entries e JOIN delivery_rider_settlements s ON s.id=e.settlement_id WHERE e.estabelecimento_id<>s.estabelecimento_id OR e.motoboy_id<>s.motoboy_id) THEN
 RAISE EXCEPTION 'Acerto com lançamento de outro motoboy/loja';
 END IF;
END $$;
