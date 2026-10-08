BEGIN;
ALTER TABLE delivery_settings ADD COLUMN IF NOT EXISTS require_delivery_proof boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS delivery_completion_proofs (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, motoboy_id integer NOT NULL REFERENCES motoboy(id),
 pedido_id integer NOT NULL REFERENCES pedido(id), stop_id bigint NOT NULL REFERENCES delivery_route_stops(id),
 conteudo bytea NOT NULL CHECK(octet_length(conteudo) BETWEEN 1 AND 4194304),
 created_at_utc timestamptz NOT NULL DEFAULT NOW(), UNIQUE(estabelecimento_id,motoboy_id,stop_id)
);
CREATE TABLE IF NOT EXISTS delivery_completions (
 operation_id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, motoboy_id integer NOT NULL REFERENCES motoboy(id),
 user_id integer NOT NULL, session_id uuid NOT NULL, session_epoch bigint NOT NULL,
 pedido_id integer NOT NULL REFERENCES pedido(id), stop_id bigint NOT NULL UNIQUE REFERENCES delivery_route_stops(id),
 payload_hash text NOT NULL, receipt jsonb NOT NULL,
 completed_at_utc timestamptz NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_delivery_completions_owner ON delivery_completions(user_id,completed_at_utc DESC);
INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261008_02_delivery_completion') ON CONFLICT DO NOTHING;
COMMIT;
