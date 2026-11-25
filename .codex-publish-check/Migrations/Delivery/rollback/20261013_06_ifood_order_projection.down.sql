BEGIN;
DO $$ BEGIN
    IF to_regclass('ifood_command_attempts') IS NOT NULL THEN
        RAISE EXCEPTION 'Rollback IF-08 requer remover IF-09 primeiro';
    END IF;
END $$;
LOCK TABLE ifood_order_event_history,ifood_dispute_settlements,ifood_order_snapshots,ifood_disputes,pedido IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_event_history) OR EXISTS(SELECT 1 FROM ifood_dispute_settlements)
       OR EXISTS(SELECT 1 FROM ifood_order_snapshots WHERE status_occurred_at_utc IS NOT NULL OR preparation_occurred_at_utc IS NOT NULL
          OR data_occurred_at_utc IS NOT NULL OR cancellation_occurred_at_utc IS NOT NULL OR tracking_occurred_at_utc IS NOT NULL OR NOT data_is_current)
       OR EXISTS(SELECT 1 FROM ifood_disputes WHERE opened_at_utc IS NOT NULL OR settled_at_utc IS NOT NULL) THEN
        RAISE EXCEPTION 'Rollback IF-08 recusado: projecao em uso';
    END IF;
END $$;
CREATE OR REPLACE FUNCTION ifood_protect_imported_order() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM ifood_order_links WHERE pedido_id=OLD.id AND estabelecimento_id=OLD.id_estabelecimento AND NOT zippy_delivery_enabled)
       AND (NEW.status_pedido IS DISTINCT FROM OLD.status_pedido OR NEW.motoboy_responsavel IS DISTINCT FROM OLD.motoboy_responsavel) THEN
        RAISE EXCEPTION 'Pedido iFood requer operacao oficial' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END $$;
DROP TABLE ifood_dispute_settlements;
DROP TABLE ifood_order_event_history;
ALTER TABLE ifood_disputes DROP COLUMN IF EXISTS opened_at_utc,DROP COLUMN IF EXISTS settled_at_utc;
ALTER TABLE ifood_order_snapshots DROP COLUMN IF EXISTS status_occurred_at_utc,DROP COLUMN IF EXISTS preparation_occurred_at_utc,
    DROP COLUMN IF EXISTS data_occurred_at_utc,DROP COLUMN IF EXISTS cancellation_occurred_at_utc,DROP COLUMN IF EXISTS tracking_occurred_at_utc,
    DROP COLUMN IF EXISTS cancellation_state,DROP COLUMN IF EXISTS delivery_state,DROP COLUMN IF EXISTS data_is_current;
DELETE FROM delivery_tracking_schema_versions WHERE version='20261013_06_ifood_order_projection';
COMMIT;
