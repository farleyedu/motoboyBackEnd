BEGIN;
ALTER TABLE ifood_reconciliation_audit DROP CONSTRAINT IF EXISTS ck_ifood_reconciliation_action;
ALTER TABLE ifood_reconciliation_audit ADD CONSTRAINT ck_ifood_reconciliation_action
    CHECK (action IN ('requeue','dismiss','resolve_review','refresh_details','adjust_location'));
ALTER TABLE ifood_reconciliation_audit ADD COLUMN IF NOT EXISTS details JSONB NULL;
-- Coordenadas padrão 0,0 também bloqueiam a logística. As flags da loja são preservadas.
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
       OR r.lat::double precision NOT BETWEEN -90 AND 90 OR r.lon::double precision NOT BETWEEN -180 AND 180
       OR (abs(r.lat::double precision)<=0.0001 AND abs(r.lon::double precision)<=0.0001) THEN RETURN 'IFOOD_DELIVERY_LOCATION_REQUIRED'; END IF;
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


INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261018_02_ifood_delivery_location') ON CONFLICT DO NOTHING;
COMMIT;
