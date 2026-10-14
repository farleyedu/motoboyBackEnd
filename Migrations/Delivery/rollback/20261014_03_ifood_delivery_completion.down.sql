BEGIN;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ifood_delivery_completions) THEN
  RAISE EXCEPTION 'Rollback IF-14 recusado: preservar operações e evidências de conclusão';
 END IF;
END $$;

CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE blocked text;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id) THEN RETURN NEW; END IF;
    IF NEW.id_estabelecimento IS DISTINCT FROM OLD.id_estabelecimento THEN RAISE EXCEPTION 'IFOOD_DELIVERY_TENANT_CHANGE' USING ERRCODE='23514'; END IF;
    IF NEW.status_pedido IS NOT DISTINCT FROM OLD.status_pedido AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel THEN RETURN NEW; END IF;
    IF NEW.status_pedido IN(4,6) AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel
       AND ifood_delivery_terminal_proof(OLD.id_estabelecimento,OLD.id,CASE WHEN NEW.status_pedido=4 THEN 'CANCELLED' ELSE 'CONCLUDED' END) THEN RETURN NEW; END IF;
    IF NEW.status_pedido NOT IN(1,2,5,8) THEN RAISE EXCEPTION 'IFOOD_ORDER_MANAGED_EXTERNALLY' USING ERRCODE='23514'; END IF;
    blocked:=ifood_delivery_blocked_code(OLD.id_estabelecimento,OLD.id,NEW.status_pedido=2);
    IF blocked IS NOT NULL THEN RAISE EXCEPTION '%',blocked USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION ifood_protect_route_stop() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE blocked text;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=NEW.pedido_id) THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' AND NEW.pedido_id=OLD.pedido_id AND NEW.estabelecimento_id=OLD.estabelecimento_id
       AND NEW.motoboy_id=OLD.motoboy_id AND NEW.stop_status=OLD.stop_status
       AND NEW.picked_up_at_utc IS NOT DISTINCT FROM OLD.picked_up_at_utc
       AND NEW.offered_at_utc IS NOT DISTINCT FROM OLD.offered_at_utc THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' AND (NEW.pedido_id<>OLD.pedido_id OR NEW.estabelecimento_id<>OLD.estabelecimento_id) THEN
        RAISE EXCEPTION 'IFOOD_DELIVERY_TENANT_CHANGE' USING ERRCODE='23514';
    END IF;
    IF NEW.stop_status IN('completed','canceled') THEN
        IF NEW.stop_status='canceled' AND ifood_delivery_terminal_proof(NEW.estabelecimento_id,NEW.pedido_id,'CANCELLED') THEN RETURN NEW; END IF;
        RAISE EXCEPTION 'IFOOD_ORDER_MANAGED_EXTERNALLY' USING ERRCODE='23514';
    END IF;
    IF NEW.stop_status='removed' AND ifood_delivery_terminal_proof(NEW.estabelecimento_id,NEW.pedido_id,'CONCLUDED') THEN RETURN NEW; END IF;
    IF NEW.stop_status IN('assigned','en_route') THEN
        blocked:=ifood_delivery_blocked_code(NEW.estabelecimento_id,NEW.pedido_id,NEW.stop_status='en_route' OR NEW.picked_up_at_utc IS NOT NULL);
        IF blocked IS NOT NULL THEN RAISE EXCEPTION '%',blocked USING ERRCODE='23514'; END IF;
    END IF;
    RETURN NEW;
END $$;

DROP FUNCTION IF EXISTS ifood_completion_local_proof(uuid,integer,bigint,integer);
DROP TABLE ifood_delivery_completions;
DROP INDEX IF EXISTS ux_ifood_completion_stop_identity;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261014_03_ifood_delivery_completion';
COMMIT;