BEGIN;
-- IF-14: passiva. Nenhuma loja, pedido ou flag é habilitada.
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_completion_stop_identity ON delivery_route_stops(id,estabelecimento_id,pedido_id);
CREATE TABLE IF NOT EXISTS ifood_delivery_completions (
 operation_id uuid PRIMARY KEY CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
 estabelecimento_id uuid NOT NULL, merchant_id uuid NOT NULL, external_order_id uuid NOT NULL,
 pedido_id int NOT NULL, stop_id bigint NULL, motoboy_id int NOT NULL, user_id int NOT NULL CHECK(user_id>0),
 kind text NOT NULL DEFAULT 'delivery' CHECK(kind IN('delivery','takeout')),
 session_id uuid NOT NULL, session_epoch bigint NOT NULL, expected_revision bigint NOT NULL CHECK(expected_revision>0),
 payload_hash text NOT NULL, request jsonb NOT NULL CHECK(jsonb_typeof(request)='object' AND COALESCE(request->>'Codigo','')=''),
 manifest jsonb NOT NULL CHECK(jsonb_typeof(manifest)='object'),
 code_envelope text NULL CHECK(code_envelope IS NULL OR code_envelope LIKE 'v1.%'),
 display_number text NULL,order_total numeric(18,2) NULL CHECK(order_total>=0),
 amount_to_collect numeric(18,2) NOT NULL CHECK(amount_to_collect>=0),paid_before bool NOT NULL,
 state text NOT NULL DEFAULT 'queued' CHECK(state IN('queued','processing','uncertain','verified','succeeded','rejected','review')),
 code_checked bool NOT NULL DEFAULT false,evidence text NULL CHECK(evidence IN('verify_delivery_code','concluded_event')),
 lease_id uuid NULL,lease_until_utc timestamptz NULL,send_started_at_utc timestamptz NULL,
 created_at_utc timestamptz NOT NULL DEFAULT NOW(),expires_at_utc timestamptz NOT NULL DEFAULT (NOW()+INTERVAL '10 minutes'),
 next_attempt_at_utc timestamptz NOT NULL DEFAULT NOW(),finished_at_utc timestamptz NULL,
 http_status int NULL,last_error_code text NULL,finalization_transaction_id xid8 NULL,
 pickup_receipt jsonb NULL CHECK(pickup_receipt IS NULL OR (kind='takeout' AND jsonb_typeof(pickup_receipt)='object')),
 FOREIGN KEY(merchant_id,external_order_id,estabelecimento_id) REFERENCES ifood_order_links(merchant_id,external_order_id,estabelecimento_id),
 FOREIGN KEY(stop_id,estabelecimento_id,pedido_id) REFERENCES delivery_route_stops(id,estabelecimento_id,pedido_id),
 CHECK((state='processing' AND lease_id IS NOT NULL AND lease_until_utc IS NOT NULL) OR (state<>'processing' AND lease_id IS NULL AND lease_until_utc IS NULL)),
 CHECK(state NOT IN('verified','succeeded') OR (send_started_at_utc IS NOT NULL AND evidence IS NOT NULL)),
 CHECK(send_started_at_utc IS NULL OR code_envelope IS NULL),
 CHECK((kind='delivery' AND stop_id IS NOT NULL AND motoboy_id>0 AND session_id<>'00000000-0000-0000-0000-000000000000')
 OR (kind='takeout' AND stop_id IS NULL AND motoboy_id=0 AND session_id='00000000-0000-0000-0000-000000000000' AND session_epoch=0))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_completion_active_order ON ifood_delivery_completions(estabelecimento_id,pedido_id) WHERE state<>'rejected';
CREATE INDEX IF NOT EXISTS ix_ifood_completion_pending ON ifood_delivery_completions(next_attempt_at_utc,created_at_utc) WHERE state='queued';
CREATE INDEX IF NOT EXISTS ix_ifood_completion_owner ON ifood_delivery_completions(user_id,operation_id);
CREATE OR REPLACE FUNCTION ifood_completion_local_proof(store uuid,pedido integer,stop bigint DEFAULT NULL,rider integer DEFAULT NULL)
RETURNS boolean LANGUAGE sql AS $$
SELECT EXISTS(SELECT 1 FROM ifood_delivery_completions o
 JOIN ifood_connections c USING(merchant_id,estabelecimento_id)
 JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
 WHERE o.estabelecimento_id=store AND o.pedido_id=pedido AND (stop IS NULL OR o.stop_id=stop) AND (rider IS NULL OR o.motoboy_id=rider)
 AND o.state='verified' AND o.finalization_transaction_id=pg_current_xact_id() AND o.send_started_at_utc IS NOT NULL
 AND c.status='authorized' AND c.authorized_at_utc IS NOT NULL AND c.revoked_at_utc IS NULL AND s.remote_status<>'CANCELLED'
 AND (o.evidence='verify_delivery_code' AND o.code_checked OR o.evidence='concluded_event' AND ifood_delivery_terminal_proof(store,pedido,'CONCLUDED')))
$$;

CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE blocked text;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id) THEN RETURN NEW; END IF;
    IF NEW.id_estabelecimento IS DISTINCT FROM OLD.id_estabelecimento THEN RAISE EXCEPTION 'IFOOD_DELIVERY_TENANT_CHANGE' USING ERRCODE='23514'; END IF;
    IF NEW.status_pedido IS NOT DISTINCT FROM OLD.status_pedido AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel THEN RETURN NEW; END IF;
    IF NEW.status_pedido IN(4,6) AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel
       AND ifood_delivery_terminal_proof(OLD.id_estabelecimento,OLD.id,CASE WHEN NEW.status_pedido=4 THEN 'CANCELLED' ELSE 'CONCLUDED' END) THEN RETURN NEW; END IF;
    IF NEW.status_pedido=3 AND NEW.motoboy_responsavel IS NOT DISTINCT FROM OLD.motoboy_responsavel
       AND ifood_completion_local_proof(OLD.id_estabelecimento,OLD.id) THEN RETURN NEW; END IF;
    IF EXISTS(SELECT 1 FROM ifood_delivery_completions WHERE estabelecimento_id=OLD.id_estabelecimento AND pedido_id=OLD.id AND state<>'rejected') THEN
        RAISE EXCEPTION 'IFOOD_COMPLETION_ACTIVE_OPERATION' USING ERRCODE='23514';
    END IF;
    IF NEW.status_pedido NOT IN(1,2,5,8) THEN RAISE EXCEPTION 'IFOOD_ORDER_MANAGED_EXTERNALLY' USING ERRCODE='23514'; END IF;
    blocked:=ifood_delivery_blocked_code(OLD.id_estabelecimento,OLD.id,NEW.status_pedido=2);
    IF blocked IS NOT NULL THEN RAISE EXCEPTION '%',blocked USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION ifood_protect_route_stop() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE blocked text;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=NEW.pedido_id) THEN RETURN NEW; END IF;
    IF NEW.stop_status='completed' AND ifood_completion_local_proof(NEW.estabelecimento_id,NEW.pedido_id,NEW.id,NEW.motoboy_id) THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' AND (NEW.stop_status<>OLD.stop_status OR NEW.motoboy_id<>OLD.motoboy_id OR NEW.pedido_id<>OLD.pedido_id OR NEW.picked_up_at_utc IS DISTINCT FROM OLD.picked_up_at_utc)
       AND EXISTS(SELECT 1 FROM ifood_delivery_completions WHERE stop_id=OLD.id AND state IN('queued','processing','uncertain','verified','review'))
       AND NOT ifood_delivery_terminal_proof(OLD.estabelecimento_id,OLD.pedido_id,'CANCELLED')
       AND NOT ifood_delivery_terminal_proof(OLD.estabelecimento_id,OLD.pedido_id,'CONCLUDED') THEN
        RAISE EXCEPTION 'IFOOD_COMPLETION_ACTIVE_OPERATION' USING ERRCODE='23514';
    END IF;
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

INSERT INTO delivery_tracking_schema_versions(version)VALUES('20261014_03_ifood_delivery_completion') ON CONFLICT DO NOTHING;
COMMIT;
