BEGIN;

-- A flag por pedido continua FALSE. IF-15 deve autorizar a habilitação; esta migration não ativa lojas.
CREATE OR REPLACE FUNCTION ifood_delivery_blocked_code(store uuid, pedido integer, departure boolean DEFAULT false)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE r record; scheduled timestamptz;
BEGIN
    SELECT l.zippy_delivery_enabled,s.*,c.status AS connection_status,c.authorized_at_utc,c.revoked_at_utc,
           p.latitude AS lat,p.longitude AS lon,p.endereco_entrega AS address
      INTO r FROM ifood_order_links l
      JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
      JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
      JOIN pedido p ON p.id=l.pedido_id AND p.id_estabelecimento=l.estabelecimento_id
     WHERE l.estabelecimento_id=store AND l.pedido_id=pedido FOR SHARE OF s,c;
    IF NOT FOUND THEN
        IF EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=pedido) THEN RETURN 'IFOOD_DELIVERY_CONTEXT_UNAVAILABLE'; END IF;
        RETURN NULL;
    END IF;
    IF r.order_type<>'DELIVERY' OR r.delivered_by IS DISTINCT FROM 'MERCHANT' THEN RETURN 'IFOOD_DELIVERY_NOT_APPLICABLE'; END IF;
    IF NOT r.zippy_delivery_enabled THEN RETURN 'IFOOD_DELIVERY_DISABLED'; END IF;
    IF r.connection_status<>'authorized' OR r.authorized_at_utc IS NULL OR r.revoked_at_utc IS NOT NULL THEN RETURN 'IFOOD_MERCHANT_NOT_AUTHORIZED'; END IF;
    IF r.remote_status IN('CANCELLED','CONCLUDED') THEN RETURN 'IFOOD_ORDER_TERMINAL'; END IF;
    IF NOT r.data_is_current OR r.synchronization_state<>'synced' THEN RETURN 'IFOOD_DELIVERY_REVIEW_REQUIRED'; END IF;
    IF r.remote_status IS NULL OR r.remote_status NOT IN('CONFIRMED','DISPATCHED') THEN RETURN 'IFOOD_DELIVERY_NOT_CONFIRMED'; END IF;
    IF r.cancellation_state NOT IN('none','rejected','') THEN RETURN 'IFOOD_DELIVERY_CANCELLATION_PENDING'; END IF;
    IF EXISTS(SELECT 1 FROM ifood_outbound_commands o WHERE o.merchant_id=r.merchant_id AND o.external_order_id=r.external_order_id
       AND o.estabelecimento_id=store AND o.state IN('queued','processing','uncertain','review')) THEN RETURN 'IFOOD_COMMAND_ACTIVE_OPERATION'; END IF;
    IF NULLIF(BTRIM(r.address),'') IS NULL OR r.lat IS NULL OR r.lon IS NULL
       OR r.lat::double precision NOT BETWEEN -90 AND 90 OR r.lon::double precision NOT BETWEEN -180 AND 180 THEN RETURN 'IFOOD_DELIVERY_LOCATION_REQUIRED'; END IF;
    IF departure THEN
        IF r.preparation_state<>'ready' THEN RETURN 'IFOOD_DELIVERY_NOT_READY'; END IF;
        IF r.remote_status<>'DISPATCHED' THEN RETURN 'IFOOD_DELIVERY_DISPATCH_PENDING'; END IF;
        IF r.payload->>'orderTiming'='SCHEDULED' THEN
            BEGIN scheduled := (COALESCE(r.payload->'schedule',r.payload->'scheduled')->>'deliveryDateTimeStart')::timestamptz;
            EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow THEN RETURN 'IFOOD_DELIVERY_SCHEDULE_REQUIRED'; END;
            IF scheduled IS NULL THEN RETURN 'IFOOD_DELIVERY_SCHEDULE_REQUIRED'; END IF;
            IF clock_timestamp()<scheduled THEN RETURN 'IFOOD_DELIVERY_BEFORE_WINDOW'; END IF;
        ELSIF r.payload->>'orderTiming' IS DISTINCT FROM 'IMMEDIATE' THEN RETURN 'IFOOD_DELIVERY_SCHEDULE_REQUIRED'; END IF;
    END IF;
    RETURN NULL;
END $$;

-- O evento terminal deve pertencer à transação e reserva atuais, inclusive quando a logística estava habilitada.
CREATE OR REPLACE FUNCTION ifood_delivery_terminal_proof(store uuid,pedido integer,terminal text)
RETURNS boolean LANGUAGE sql AS $$
SELECT EXISTS(SELECT 1 FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
 JOIN ifood_order_event_history h USING(merchant_id,external_order_id,estabelecimento_id)
 JOIN ifood_events_inbox e ON e.id=h.inbox_id
 JOIN ifood_connections c ON c.merchant_id=l.merchant_id AND c.estabelecimento_id=l.estabelecimento_id
 WHERE l.pedido_id=pedido AND l.estabelecimento_id=store AND s.remote_status=terminal
 AND h.outcome='applied' AND h.event_code=terminal AND h.projection_transaction_id=pg_current_xact_id()
 AND e.state='processing' AND e.lease_until_utc>clock_timestamp()
 AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL)
$$;

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
DROP TRIGGER IF EXISTS ifood_route_stop_guard ON delivery_route_stops;
CREATE TRIGGER ifood_route_stop_guard BEFORE INSERT OR UPDATE ON delivery_route_stops FOR EACH ROW EXECUTE FUNCTION ifood_protect_route_stop();
INSERT INTO delivery_tracking_schema_versions(version)VALUES('20261014_01_ifood_delivery_routing') ON CONFLICT DO NOTHING;
COMMIT;
