BEGIN;

-- Os pedidos já aceitos continuam na rota. Novas atribuições sempre são ofertas.
ALTER TABLE delivery_settings ALTER COLUMN require_motoboy_acceptance SET DEFAULT TRUE;
UPDATE delivery_settings SET require_motoboy_acceptance = TRUE WHERE NOT require_motoboy_acceptance;
INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261010_03_aceite_obrigatorio') ON CONFLICT (version) DO NOTHING;

COMMIT;
