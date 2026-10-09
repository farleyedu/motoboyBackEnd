BEGIN;
CREATE TABLE IF NOT EXISTS delivery_offer_push_outbox (
 id BIGSERIAL PRIMARY KEY,
 offer_id UUID NOT NULL,
 token TEXT NOT NULL REFERENCES delivery_chat_push_subscription(token) ON DELETE CASCADE,
 estabelecimento_id UUID NOT NULL,
 motoboy_id INTEGER NOT NULL,
 session_id UUID NOT NULL,
 expires_at_utc TIMESTAMPTZ NOT NULL,
 state TEXT NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','sending','sent','delivered','failed','dismissed')),
 attempts INTEGER NOT NULL DEFAULT 0,
 available_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
 lease_id UUID NULL,
 lease_until_utc TIMESTAMPTZ NULL,
 ticket_id TEXT NULL,
 receipt_checked_at_utc TIMESTAMPTZ NULL,
 error_code TEXT NULL,
 created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
 UNIQUE(offer_id,token,session_id)
);
CREATE INDEX IF NOT EXISTS ix_offer_push_pending ON delivery_offer_push_outbox(available_at_utc,id) WHERE state IN ('queued','sending');
CREATE INDEX IF NOT EXISTS ix_offer_push_receipt ON delivery_offer_push_outbox(created_at_utc) WHERE state='sent';

CREATE OR REPLACE FUNCTION delivery_enqueue_offer_push() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE rider INTEGER; store UUID;
BEGIN
 IF TG_TABLE_NAME='delivery_route_stops' THEN
  IF NEW.offered_at_utc IS NULL OR NEW.offer_id IS NULL OR NEW.stop_status<>'assigned' THEN RETURN NEW; END IF;
  rider:=NEW.motoboy_id; store:=NEW.estabelecimento_id;
 ELSE
  rider:=NEW.motoboy_id; store:=NEW.estabelecimento_id;
 END IF;
 INSERT INTO delivery_offer_push_outbox(offer_id,token,estabelecimento_id,motoboy_id,session_id,expires_at_utc)
 SELECT s.offer_id,p.token,p.estabelecimento_id,p.motoboy_id,p.session_id,
   MIN(s.offered_at_utc)+make_interval(mins=>COALESCE(d.offer_timeout_minutes,2))
 FROM delivery_route_stops s
 JOIN delivery_chat_push_subscription p ON p.motoboy_id=s.motoboy_id AND p.estabelecimento_id=s.estabelecimento_id
 JOIN motoboy_active_sessions a ON a.session_id=p.session_id AND a.motoboy_id=p.motoboy_id AND a.id_estabelecimento=p.estabelecimento_id AND a.ended_at_utc IS NULL AND a.revoked_at IS NULL AND a.expires_at_utc>NOW()
 LEFT JOIN delivery_settings d ON d.estabelecimento_id=s.estabelecimento_id
 WHERE s.motoboy_id=rider AND s.estabelecimento_id=store AND s.offered_at_utc IS NOT NULL AND s.offer_id IS NOT NULL AND s.stop_status='assigned'
 GROUP BY s.offer_id,p.token,p.estabelecimento_id,p.motoboy_id,p.session_id,d.offer_timeout_minutes
 HAVING MIN(s.offered_at_utc)+make_interval(mins=>COALESCE(d.offer_timeout_minutes,2))>NOW()
 ON CONFLICT(offer_id,token,session_id) DO NOTHING;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS delivery_offer_push_stop ON delivery_route_stops;
CREATE TRIGGER delivery_offer_push_stop AFTER INSERT OR UPDATE OF offered_at_utc,offer_id ON delivery_route_stops FOR EACH ROW EXECUTE FUNCTION delivery_enqueue_offer_push();
DROP TRIGGER IF EXISTS delivery_offer_push_subscription ON delivery_chat_push_subscription;
CREATE TRIGGER delivery_offer_push_subscription AFTER INSERT OR UPDATE ON delivery_chat_push_subscription FOR EACH ROW EXECUTE FUNCTION delivery_enqueue_offer_push();
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261009_03_offer_push') ON CONFLICT DO NOTHING;
COMMIT;
