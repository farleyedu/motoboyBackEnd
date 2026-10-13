BEGIN;

-- A verificação falha se o schema anterior não foi aplicado. Não registra dependência incompleta como pronta.
DO $$
DECLARE table_name TEXT; boundary RECORD;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261013_01_ifood_integration_schema') THEN
        RAISE EXCEPTION 'Schema iFood pendente: aplique 20261013_01_ifood_integration_schema antes da verificacao';
    END IF;
    FOREACH table_name IN ARRAY ARRAY['ifood_connections','ifood_order_links','ifood_events_inbox',
        'ifood_order_snapshots','ifood_outbound_commands','ifood_disputes'] LOOP
        IF to_regclass(table_name) IS NULL THEN RAISE EXCEPTION 'Tabela iFood ausente: %', table_name; END IF;
    END LOOP;

    -- Conferir as FKs compostas críticas, não somente a existência das tabelas.
    FOR boundary IN SELECT * FROM (VALUES
        ('ifood_order_links','pedido',ARRAY['pedido_id','estabelecimento_id'],ARRAY['id','id_estabelecimento']),
        ('ifood_order_links','ifood_connections',ARRAY['merchant_id','estabelecimento_id'],ARRAY['merchant_id','estabelecimento_id']),
        ('ifood_events_inbox','ifood_connections',ARRAY['merchant_id','estabelecimento_id'],ARRAY['merchant_id','estabelecimento_id']),
        ('ifood_order_snapshots','ifood_order_links',ARRAY['merchant_id','external_order_id','estabelecimento_id'],ARRAY['merchant_id','external_order_id','estabelecimento_id']),
        ('ifood_outbound_commands','ifood_order_links',ARRAY['merchant_id','external_order_id','estabelecimento_id'],ARRAY['merchant_id','external_order_id','estabelecimento_id']),
        ('ifood_disputes','ifood_order_links',ARRAY['merchant_id','external_order_id','estabelecimento_id'],ARRAY['merchant_id','external_order_id','estabelecimento_id'])
    ) AS required(source_table,target_table,source_columns,target_columns) LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint c
            WHERE c.contype='f' AND c.convalidated
                AND c.conrelid=to_regclass(boundary.source_table)
                AND c.confrelid=to_regclass(boundary.target_table)
                AND (SELECT array_agg(a.attname::text ORDER BY k.position)
                     FROM unnest(c.conkey) WITH ORDINALITY k(number,position)
                     JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.number) = boundary.source_columns
                AND (SELECT array_agg(a.attname::text ORDER BY k.position)
                     FROM unnest(c.confkey) WITH ORDINALITY k(number,position)
                     JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.number) = boundary.target_columns
        ) THEN RAISE EXCEPTION 'Integridade iFood incompleta: % -> %', boundary.source_table, boundary.target_table;
        END IF;
    END LOOP;
END $$;

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261013_02_ifood_integration_verify') ON CONFLICT (version) DO NOTHING;
COMMIT;
