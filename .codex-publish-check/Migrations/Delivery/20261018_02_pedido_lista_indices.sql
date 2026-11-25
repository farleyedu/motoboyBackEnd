BEGIN;

-- A lista de pedidos filtra por estabelecimento e ordena por id decrescente, e a contagem roda sobre o
-- mesmo filtro. Sem indice as duas consultas varrem a tabela inteira: medido em ~5 s no painel com o
-- filtro de origem iFood. Os dois indices abaixo atendem a lista geral e a lista por canal.
CREATE INDEX IF NOT EXISTS ix_pedido_estabelecimento_id ON pedido (id_estabelecimento, id DESC);
CREATE INDEX IF NOT EXISTS ix_pedido_estabelecimento_origem_id ON pedido (id_estabelecimento, origem, id DESC);

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261018_02_pedido_lista_indices') ON CONFLICT DO NOTHING;
COMMIT;
