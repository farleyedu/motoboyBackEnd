BEGIN;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM delivery_route_stops s JOIN ifood_order_links l ON l.pedido_id=s.pedido_id AND l.estabelecimento_id=s.estabelecimento_id
       WHERE s.stop_status IN('assigned','en_route')) THEN
        RAISE EXCEPTION 'Encerre as rotas oficiais antes do rollback IF-12';
    END IF;
END $$;
-- Desliga explicitamente a log?stica antes de restaurar o bloqueio anterior.
UPDATE ifood_order_links SET zippy_delivery_enabled=FALSE;
DROP TRIGGER IF EXISTS ifood_route_stop_guard ON delivery_route_stops;
DROP FUNCTION IF EXISTS ifood_protect_route_stop();
CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id AND estabelecimento_id=OLD.id_estabelecimento AND NOT zippy_delivery_enabled)
       AND (NEW.status_pedido IS DISTINCT FROM OLD.status_pedido OR NEW.motoboy_responsavel IS DISTINCT FROM OLD.motoboy_responsavel) THEN
        -- Exceção somente para CAN projetado nesta transação, com reserva/autorização atuais.
        IF NEW.status_pedido=4 AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel
           AND EXISTS(SELECT 1 FROM ifood_order_links l JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
              JOIN ifood_order_event_history h USING(merchant_id,external_order_id,estabelecimento_id)
              JOIN ifood_events_inbox e ON e.id=h.inbox_id
              JOIN ifood_connections c ON c.merchant_id=l.merchant_id AND c.estabelecimento_id=l.estabelecimento_id
              WHERE l.pedido_id=OLD.id AND l.estabelecimento_id=OLD.id_estabelecimento AND s.remote_status='CANCELLED'
              AND h.outcome='applied' AND h.event_code='CANCELLED' AND h.projection_transaction_id=pg_current_xact_id()
              AND e.state='processing' AND e.lease_until_utc>clock_timestamp()
              AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL) THEN
            RETURN NEW;
        END IF;
        RAISE EXCEPTION 'Pedido iFood requer operacao oficial' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
DROP FUNCTION IF EXISTS ifood_delivery_terminal_proof(uuid,integer,text);
DROP FUNCTION IF EXISTS ifood_delivery_blocked_code(uuid,integer,boolean);
DELETE FROM delivery_tracking_schema_versions WHERE version='20261014_01_ifood_delivery_routing';
COMMIT;
