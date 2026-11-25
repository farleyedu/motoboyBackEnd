BEGIN;
-- Configuração global da demonstração pública, independente das lojas reais.
CREATE TABLE IF NOT EXISTS zippy_experience_location (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    latitude DOUBLE PRECISION NOT NULL CHECK (latitude BETWEEN -85 AND 85),
    longitude DOUBLE PRECISION NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    cidade VARCHAR(120),
    uf VARCHAR(2) CHECK (uf IS NULL OR uf ~ '^[A-Z]{2}$'),
    updated_by_user_id INTEGER NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO delivery_tracking_schema_versions(version)
VALUES ('20261013_03_zippy_experience_location') ON CONFLICT DO NOTHING;
COMMIT;
