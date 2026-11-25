BEGIN;

-- IF-13 (lote 2): conclusão por código oficial e entrega Zippy por loja passam a ser configuradas pela tela,
-- sem variável de ambiente. Ambas começam desligadas; nenhuma loja ou pedido é habilitado por esta migration.
ALTER TABLE ifood_app_settings ADD COLUMN IF NOT EXISTS completion_enabled BOOLEAN NOT NULL DEFAULT FALSE;
-- Valor herdado pelos pedidos NOVOS da loja no momento da importação (ifood_order_links.zippy_delivery_enabled).
-- Não altera pedidos já importados.
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS zippy_delivery_enabled BOOLEAN NOT NULL DEFAULT FALSE;

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261014_04_ifood_runtime_flags') ON CONFLICT DO NOTHING;
COMMIT;
