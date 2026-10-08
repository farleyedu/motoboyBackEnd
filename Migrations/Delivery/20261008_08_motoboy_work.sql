BEGIN;
CREATE INDEX IF NOT EXISTS ix_rider_work_history ON delivery_route_stops(estabelecimento_id,motoboy_id,updated_at_utc DESC);
CREATE TABLE IF NOT EXISTS delivery_rider_pay_plans (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, motoboy_id integer NOT NULL,
 mode text NOT NULL CHECK(mode IN ('delivery','distance','hour','shift','day','week','fortnight','month')),
 rate numeric(14,2) NOT NULL CHECK(rate>0 AND rate<=1000000), created_by integer NOT NULL,
 created_at_utc timestamptz NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_rider_plans ON delivery_rider_pay_plans(estabelecimento_id,motoboy_id,created_at_utc DESC);
CREATE TABLE IF NOT EXISTS delivery_rider_quotes (
 stop_id bigint PRIMARY KEY REFERENCES delivery_route_stops(id), quote jsonb NOT NULL
);
CREATE TABLE IF NOT EXISTS delivery_rider_settlements (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL,motoboy_id integer NOT NULL,
 status text NOT NULL DEFAULT 'draft' CHECK(status IN ('draft','reviewed','disputed','paid','received','cancelled')),
 earnings numeric(14,2) NOT NULL CHECK(earnings>=0), store_cash numeric(14,2) NOT NULL CHECK(store_cash>=0),
 cash_returned boolean NOT NULL DEFAULT FALSE, reason text, method text, reference text,
 created_by integer NOT NULL, created_at_utc timestamptz NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS delivery_rider_work_entries (
 id uuid PRIMARY KEY,estabelecimento_id uuid NOT NULL,motoboy_id integer NOT NULL,
 kind text NOT NULL CHECK(kind IN ('delivery','period')),mode text,plan_id uuid REFERENCES delivery_rider_pay_plans(id),
 amount numeric(14,2) CHECK(amount>=0),store_cash numeric(14,2) NOT NULL DEFAULT 0 CHECK(store_cash>=0),cash_confirmed boolean NOT NULL DEFAULT FALSE,
 distance_km numeric, pedido_id integer,stop_id bigint UNIQUE REFERENCES delivery_route_stops(id),
 settlement_id uuid REFERENCES delivery_rider_settlements(id),
 from_utc timestamptz NOT NULL,to_utc timestamptz NOT NULL,worked_minutes integer,
 created_by integer, CHECK(to_utc>=from_utc)
);
CREATE INDEX IF NOT EXISTS ix_rider_entries ON delivery_rider_work_entries(estabelecimento_id,motoboy_id,to_utc DESC);
CREATE TABLE IF NOT EXISTS delivery_rider_settlement_events (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,settlement_id uuid NOT NULL REFERENCES delivery_rider_settlements(id),
 actor_user_id integer NOT NULL,action text NOT NULL,details jsonb NOT NULL DEFAULT '{}',occurred_at_utc timestamptz NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS delivery_rider_support (
 id uuid PRIMARY KEY,estabelecimento_id uuid NOT NULL,motoboy_id integer NOT NULL,
 category text NOT NULL CHECK(category IN ('delivery','payment','account','location','security')),
 message text NOT NULL CHECK(length(message) BETWEEN 1 AND 2000),created_at_utc timestamptz NOT NULL DEFAULT NOW()
);
COMMIT;
