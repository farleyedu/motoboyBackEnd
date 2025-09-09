-- Dropar e recriar tabela timeline_pedido com estrutura correta
DROP TABLE IF EXISTS timeline_pedido CASCADE;

-- Recriar tabela timeline_pedido com todas as colunas necessárias
CREATE TABLE timeline_pedido (
    id BIGSERIAL PRIMARY KEY,
    pedido_id BIGINT NOT NULL REFERENCES pedido(id) ON DELETE CASCADE,
    evento VARCHAR(100) NOT NULL,
    descricao TEXT,
    status VARCHAR(50) NOT NULL DEFAULT 'pendente',
    data_evento TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    motoboy_id BIGINT REFERENCES motoboy(id),
    localizacao VARCHAR(500),
    observacoes TEXT,
    ordem_evento INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Criar índices
CREATE INDEX idx_timeline_pedido_pedido_id ON timeline_pedido(pedido_id);
CREATE INDEX idx_timeline_pedido_status ON timeline_pedido(status);
CREATE INDEX idx_timeline_pedido_data_evento ON timeline_pedido(data_evento);
CREATE INDEX idx_timeline_pedido_motoboy_id ON timeline_pedido(motoboy_id);
CREATE INDEX idx_timeline_pedido_ordem ON timeline_pedido(pedido_id, ordem_evento);

-- Inserir dados de timeline para pedidos existentes
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, ordem_evento)
SELECT 
    id as pedido_id,
    'Pedido Criado' as evento,
    CONCAT('Pedido #', id, ' criado - ', items) as descricao,
    CASE 
        WHEN status = 'delivered' THEN 'concluido'
        WHEN status = 'canceled' THEN 'cancelado'
        WHEN status = 'on_route' THEN 'em_andamento'
        ELSE 'pendente'
    END as status,
    created_at as data_evento,
    1 as ordem_evento
FROM pedido;

-- Verificar resultados
SELECT COUNT(*) as total_timeline FROM timeline_pedido;
SELECT * FROM timeline_pedido WHERE pedido_id = 1 ORDER BY ordem_evento;