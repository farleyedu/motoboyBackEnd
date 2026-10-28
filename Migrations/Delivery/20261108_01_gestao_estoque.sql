-- Gestao, Logica E3: baixa de estoque por venda. Uma baixa (acontecimento 'baixa_vendas') cobre varios pedidos; esta tabela
-- garante que um pedido nao baixe duas vezes (regra 5), mesmo em baixas diferentes. Compra, conferencia, perda e inventario
-- vivem so no livro (gestao_acontecimentos/gestao_movimentos_estoque) e nao precisam de tabela propria.
-- Imutavel (gatilho da Fundacao). Requer 20261105_01_gestao_fundacao.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_baixas_pedido (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    -- Sem FK para pedido: o livro nao depende do schema de pedidos; o servico confere que o pedido e da loja.
    pedido_id integer NOT NULL,
    modalidade varchar(10) NOT NULL CHECK (modalidade IN ('salao', 'retirada', 'delivery')),
    acontecimento_id uuid NOT NULL,
    criado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, pedido_id),
    FOREIGN KEY (estabelecimento_id, acontecimento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_baixas_pedido_acontecimento ON gestao_baixas_pedido (estabelecimento_id, acontecimento_id);

DROP TRIGGER IF EXISTS gestao_baixas_pedido_imutavel ON gestao_baixas_pedido;
CREATE TRIGGER gestao_baixas_pedido_imutavel BEFORE UPDATE OR DELETE ON gestao_baixas_pedido
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
