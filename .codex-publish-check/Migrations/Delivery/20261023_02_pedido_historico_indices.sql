BEGIN;

-- O historico filtra por estabelecimento + periodo (data_pedido) e por status. Com o filtro textual
-- LEFT(data_pedido::text, 10) nenhum indice servia; a API agora compara a coluna direto (quando ela e date/timestamp)
-- e estes indices atendem o periodo e o filtro de status da lista. Idempotente.
CREATE INDEX IF NOT EXISTS ix_pedido_estabelecimento_data ON pedido (id_estabelecimento, data_pedido DESC);
CREATE INDEX IF NOT EXISTS ix_pedido_estabelecimento_status_id ON pedido (id_estabelecimento, status_pedido, id DESC);

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261023_02_pedido_historico_indices') ON CONFLICT DO NOTHING;
COMMIT;
