BEGIN;
CREATE TABLE IF NOT EXISTS delivery_route_runs (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
 motoboy_id integer NOT NULL REFERENCES motoboy(id), started_at_utc timestamptz NOT NULL,
 ended_at_utc timestamptz, status text NOT NULL DEFAULT 'active' CHECK(status IN ('active','returning','completed','interrupted'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_route_runs_open ON delivery_route_runs(estabelecimento_id,motoboy_id) WHERE ended_at_utc IS NULL;
CREATE INDEX IF NOT EXISTS ix_route_runs_owner ON delivery_route_runs(estabelecimento_id,motoboy_id,started_at_utc DESC,id DESC);
CREATE TABLE IF NOT EXISTS delivery_route_run_stops (
 run_id uuid NOT NULL REFERENCES delivery_route_runs(id), stop_id bigint PRIMARY KEY REFERENCES delivery_route_stops(id),
 pedido_id integer NOT NULL, position integer NOT NULL, district text, latitude double precision, longitude double precision,
 status text NOT NULL, picked_up_at_utc timestamptz, arrived_at_utc timestamptz, updated_at_utc timestamptz NOT NULL,
 manifest jsonb, CONSTRAINT route_stop_coords CHECK((latitude IS NULL OR latitude BETWEEN -90 AND 90) AND (longitude IS NULL OR longitude BETWEEN -180 AND 180))
);
CREATE INDEX IF NOT EXISTS ix_route_run_stops ON delivery_route_run_stops(run_id,position,stop_id);
CREATE TABLE IF NOT EXISTS delivery_route_run_points (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, run_id uuid NOT NULL REFERENCES delivery_route_runs(id),
 sample_id uuid NOT NULL, session_id uuid NOT NULL, latitude double precision NOT NULL CHECK(latitude BETWEEN -90 AND 90),
 longitude double precision NOT NULL CHECK(longitude BETWEEN -180 AND 180), captured_at_utc timestamptz NOT NULL,
 UNIQUE(run_id,sample_id)
);
CREATE INDEX IF NOT EXISTS ix_route_run_path ON delivery_route_run_points(run_id,captured_at_utc,id);
CREATE INDEX IF NOT EXISTS ix_route_run_point_retention ON delivery_route_run_points(captured_at_utc);

CREATE OR REPLACE FUNCTION delivery_capture_route_stop() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE run uuid; oldrun uuid;
BEGIN
 IF NEW.picked_up_at_utc IS NOT NULL AND (TG_OP='INSERT' OR OLD.picked_up_at_utc IS NULL)
    AND NEW.stop_status IN ('assigned','en_route') THEN
  -- A fila já está bloqueada pela transação operacional. Uma retirada identifica uma viagem, não um dia inteiro.
  SELECT id INTO oldrun FROM delivery_route_runs WHERE estabelecimento_id=NEW.estabelecimento_id AND motoboy_id=NEW.motoboy_id AND ended_at_utc IS NULL FOR UPDATE;
  IF oldrun IS NOT NULL AND NOT EXISTS(SELECT 1 FROM delivery_route_run_stops WHERE run_id=oldrun AND status IN ('assigned','en_route')) THEN
   UPDATE delivery_route_runs SET ended_at_utc=NOW(),status='completed' WHERE id=oldrun;
   oldrun:=NULL;
  END IF;
  run:=oldrun;
  IF run IS NULL THEN
   INSERT INTO delivery_route_runs(estabelecimento_id,motoboy_id,started_at_utc) VALUES(NEW.estabelecimento_id,NEW.motoboy_id,NEW.picked_up_at_utc) RETURNING id INTO run;
  END IF;
  INSERT INTO delivery_route_run_stops(run_id,stop_id,pedido_id,position,district,latitude,longitude,status,picked_up_at_utc,arrived_at_utc,updated_at_utc,manifest)
  SELECT run,NEW.id,NEW.pedido_id,NEW.position,p.entrega_bairro,
   CASE WHEN p.latitude::text ~ '^-?[0-9]+(\.[0-9]+)?$' THEN CASE WHEN p.latitude::text::double precision BETWEEN -90 AND 90 THEN p.latitude::text::double precision END END,
   CASE WHEN p.longitude::text ~ '^-?[0-9]+(\.[0-9]+)?$' THEN CASE WHEN p.longitude::text::double precision BETWEEN -180 AND 180 THEN p.longitude::text::double precision END END,
   NEW.stop_status,NEW.picked_up_at_utc,NEW.arrived_at_utc,COALESCE(NEW.updated_at_utc,NOW()),
   (SELECT manifest FROM delivery_order_item_checks WHERE stop_id=NEW.id AND stage='pickup')
  FROM pedido p WHERE p.id=NEW.pedido_id AND p.id_estabelecimento=NEW.estabelecimento_id ON CONFLICT(stop_id) DO NOTHING;
 END IF;
 UPDATE delivery_route_run_stops h SET status=CASE WHEN r.motoboy_id=NEW.motoboy_id AND r.estabelecimento_id=NEW.estabelecimento_id THEN NEW.stop_status ELSE 'transferred' END,
  arrived_at_utc=NEW.arrived_at_utc,updated_at_utc=COALESCE(NEW.updated_at_utc,NOW())
 FROM delivery_route_runs r WHERE h.stop_id=NEW.id AND h.run_id=r.id AND r.ended_at_utc IS NULL;
 UPDATE delivery_route_runs r SET status='returning' WHERE r.ended_at_utc IS NULL AND EXISTS(SELECT 1 FROM delivery_route_run_stops WHERE run_id=r.id AND stop_id=NEW.id)
  AND NOT EXISTS(SELECT 1 FROM delivery_route_run_stops WHERE run_id=r.id AND status IN ('assigned','en_route'));
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS delivery_capture_route_stop ON delivery_route_stops;
CREATE TRIGGER delivery_capture_route_stop AFTER INSERT OR UPDATE OF picked_up_at_utc,stop_status,motoboy_id,arrived_at_utc ON delivery_route_stops FOR EACH ROW EXECUTE FUNCTION delivery_capture_route_stop();

CREATE OR REPLACE FUNCTION delivery_capture_route_point() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 -- Os samples já passaram por autorização/validação da sessão. Preservar só pontos precisos dentro da viagem.
 IF NEW.quality NOT IN ('stale','low','rejected') AND (NEW.accuracy_meters IS NULL OR NEW.accuracy_meters<=100)
    AND NEW.captured_at_utc<=NOW()+interval '30 seconds' THEN
  INSERT INTO delivery_route_run_points(run_id,sample_id,session_id,latitude,longitude,captured_at_utc)
  SELECT r.id,NEW.sample_id,NEW.session_id,NEW.latitude,NEW.longitude,NEW.captured_at_utc FROM delivery_route_runs r
  WHERE r.estabelecimento_id=NEW.estabelecimento_id AND r.motoboy_id=NEW.motoboy_id
   AND NEW.captured_at_utc>=r.started_at_utc AND NEW.captured_at_utc<=COALESCE(r.ended_at_utc,NOW()+interval '30 seconds')
   AND r.started_at_utc>=NOW()-interval '90 days'
  ON CONFLICT(run_id,sample_id) DO NOTHING;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS delivery_capture_route_point ON motoboy_location_samples;
CREATE TRIGGER delivery_capture_route_point AFTER INSERT ON motoboy_location_samples FOR EACH ROW EXECUTE FUNCTION delivery_capture_route_point();

CREATE OR REPLACE FUNCTION delivery_close_route_run() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.route_state='idle' AND OLD.route_state IS DISTINCT FROM 'idle' THEN
  UPDATE delivery_route_runs r SET ended_at_utc=NOW(),status=CASE WHEN EXISTS(SELECT 1 FROM delivery_route_run_stops WHERE run_id=r.id AND status IN ('assigned','en_route')) THEN 'interrupted' ELSE 'completed' END
   WHERE r.estabelecimento_id=NEW.estabelecimento_id AND r.motoboy_id=NEW.motoboy_id AND r.ended_at_utc IS NULL;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS delivery_close_route_run ON delivery_motoboy_route;
CREATE TRIGGER delivery_close_route_run AFTER UPDATE OF route_state ON delivery_motoboy_route FOR EACH ROW EXECUTE FUNCTION delivery_close_route_run();
INSERT INTO delivery_tracking_schema_versions(version) VALUES('20261009_04_route_history') ON CONFLICT DO NOTHING;
COMMIT;
