-- Script para criar tabelas timeline_pedido e itens_pedido com dados fictícios

-- Criar tabela timeline_pedido se não existir
CREATE TABLE IF NOT EXISTS timeline_pedido (
    id SERIAL PRIMARY KEY,
    pedido_id INTEGER NOT NULL,
    evento VARCHAR(100) NOT NULL,
    descricao TEXT,
    evento_ordem INTEGER NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (pedido_id) REFERENCES pedido(id)
);

-- Criar tabela itens_pedido se não existir
CREATE TABLE IF NOT EXISTS itens_pedido (
    id SERIAL PRIMARY KEY,
    pedido_id INTEGER NOT NULL,
    nome VARCHAR(255) NOT NULL,
    quantidade INTEGER NOT NULL DEFAULT 1,
    valor_unitario DECIMAL(10,2) NOT NULL,
    valor_total DECIMAL(10,2) NOT NULL,
    tipo VARCHAR(100),
    observacoes TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (pedido_id) REFERENCES pedido(id)
);

-- Inserir dados fictícios para itens_pedido
INSERT INTO itens_pedido (pedido_id, nome, quantidade, valor_unitario, valor_total, tipo, observacoes) VALUES
(1, 'Big Mac', 2, 25.90, 51.80, 'Hambúrguer', 'Sem cebola'),
(1, 'Batata Frita Grande', 1, 12.50, 12.50, 'Acompanhamento', ''),
(1, 'Coca-Cola 500ml', 2, 8.90, 17.80, 'Bebida', 'Gelada'),
(2, 'Pizza Margherita', 1, 45.00, 45.00, 'Pizza', 'Massa fina'),
(2, 'Refrigerante 2L', 1, 12.00, 12.00, 'Bebida', ''),
(3, 'X-Bacon', 1, 28.50, 28.50, 'Hambúrguer', 'Ponto da carne: mal passado'),
(3, 'Onion Rings', 1, 15.90, 15.90, 'Acompanhamento', ''),
(4, 'Sushi Combo', 1, 65.00, 65.00, 'Japonês', '20 peças variadas'),
(4, 'Temaki Salmão', 2, 18.50, 37.00, 'Japonês', ''),
(5, 'Açaí 500ml', 1, 22.00, 22.00, 'Sobremesa', 'Com granola e banana');

-- Inserir dados fictícios para timeline_pedido
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem) VALUES
-- Pedido 1 (Entregue)
(1, 'PEDIDO_CRIADO', 'Pedido criado pelo cliente', 1),
(1, 'PAGAMENTO_CONFIRMADO', 'Pagamento aprovado via cartão', 2),
(1, 'PREPARANDO', 'Restaurante iniciou preparo', 3),
(1, 'PRONTO_PARA_RETIRADA', 'Pedido pronto para coleta', 4),
(1, 'COLETADO', 'Motoboy coletou o pedido', 5),
(1, 'A_CAMINHO', 'Motoboy a caminho do cliente', 6),
(1, 'ENTREGUE', 'Pedido entregue ao cliente', 7),

-- Pedido 2 (A caminho)
(2, 'PEDIDO_CRIADO', 'Pedido criado pelo cliente', 1),
(2, 'PAGAMENTO_CONFIRMADO', 'Pagamento aprovado via PIX', 2),
(2, 'PREPARANDO', 'Restaurante iniciou preparo', 3),
(2, 'PRONTO_PARA_RETIRADA', 'Pedido pronto para coleta', 4),
(2, 'COLETADO', 'Motoboy coletou o pedido', 5),
(2, 'A_CAMINHO', 'Motoboy a caminho do cliente', 6),

-- Pedido 3 (Preparando)
(3, 'PEDIDO_CRIADO', 'Pedido criado pelo cliente', 1),
(3, 'PAGAMENTO_CONFIRMADO', 'Pagamento aprovado via cartão', 2),
(3, 'PREPARANDO', 'Restaurante iniciou preparo', 3),

-- Pedido 4 (Aguardando pagamento)
(4, 'PEDIDO_CRIADO', 'Pedido criado pelo cliente', 1),

-- Pedido 5 (Cancelado)
(5, 'PEDIDO_CRIADO', 'Pedido criado pelo cliente', 1),
(5, 'CANCELADO', 'Pedido cancelado pelo cliente', 2);

-- Criar índices para performance
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_pedido_id ON timeline_pedido(pedido_id);
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_evento_ordem ON timeline_pedido(pedido_id, evento_ordem);
CREATE INDEX IF NOT EXISTS idx_itens_pedido_pedido_id ON itens_pedido(pedido_id);

-- Verificar dados inseridos
SELECT 'TIMELINE INSERIDA:' as info, COUNT(*) as total FROM timeline_pedido;
SELECT 'ITENS INSERIDOS:' as info, COUNT(*) as total FROM itens_pedido;