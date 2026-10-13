BEGIN;

-- Rollback somente da infraestrutura passiva IF-04. Não descartar obrigações reais da integração.
-- Recusa qualquer dado iFood; rollback com integração em uso precisa de plano de preservação próprio.
DO $$
DECLARE table_name TEXT; has_data BOOLEAN;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['ifood_connections','ifood_order_links','ifood_events_inbox',
        'ifood_order_snapshots','ifood_outbound_commands','ifood_disputes'] LOOP
        IF to_regclass(table_name) IS NOT NULL THEN
            -- Bloqueia novas escritas entre a checagem e o DROP nesta transação.
            EXECUTE format('LOCK TABLE %I IN ACCESS EXCLUSIVE MODE', table_name);
            EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I LIMIT 1)', table_name) INTO has_data;
            IF has_data THEN RAISE EXCEPTION 'Rollback iFood bloqueado: % contem dados que devem ser preservados', table_name; END IF;
        END IF;
    END LOOP;
END $$;

DROP TABLE IF EXISTS ifood_disputes;
DROP TABLE IF EXISTS ifood_outbound_commands;
DROP TABLE IF EXISTS ifood_order_snapshots;
DROP TABLE IF EXISTS ifood_events_inbox;
DROP TABLE IF EXISTS ifood_order_links;
DROP TABLE IF EXISTS ifood_connections;
DROP INDEX IF EXISTS ux_pedido_ifood_store_identity;
DELETE FROM delivery_tracking_schema_versions WHERE version IN ('20261013_01_ifood_integration_schema','20261013_02_ifood_integration_verify');
COMMIT;
