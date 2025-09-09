-- Script simples para criar timeline_pedido
-- Execute este script diretamente no PostgreSQL

-- Drop da tabela se existir
DROP TABLE IF EXISTS timeline_pedido CASCADE;

-- Criar tabela timeline_pedido com todas as colunas necessárias
CREATE TABLE timeline_pedido (
    id BIGSERIAL PRIMARY KEY,
    pedido_id BIGINT NOT NULL,
    evento VARCHAR(50) NOT NULL,
    descricao TEXT,
    status VARCHAR(30),
    data_evento TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    motoboy_id BIGINT,
    localizacao TEXT,
    observacoes TEXT,
    ordem_evento INTEGER DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (pedido_id) REFERENCES pedido(id),
    FOREIGN KEY (motoboy_id) REFERENCES motoboy(id)
);

-- Criar índices
CREATE INDEX idx_timeline_pedido_pedido_id ON timeline_pedido(pedido_id);
CREATE INDEX idx_timeline_pedido_status ON timeline_pedido(status);
CREATE INDEX idx_timeline_pedido_data_evento ON timeline_pedido(data_evento);
CREATE INDEX idx_timeline_pedido_motoboy_id ON timeline_pedido(motoboy_id);
CREATE INDEX idx_timeline_pedido_ordem ON timeline_pedido(pedido_id, ordem_evento);

-- Inserir dados iniciais baseados nos pedidos existentes
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, ordem_evento)
SELECT 
    id as pedido_id,
    'pedido_criado' as evento,
    'Pedido criado no sistema' as descricao,
    status,
    created_at as data_evento,
    1 as ordem_evento
FROM pedido;

-- Inserir eventos de atribuição para pedidos que têm motoboy
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, ordem_evento)
SELECT 
    id as pedido_id,
    'pedido_atribuido' as evento,
    'Pedido atribuído ao motoboy' as descricao,
    status,
    updated_at as data_evento,
    motoboy_id,
    2 as ordem_evento
FROM pedido 
WHERE motoboy_id IS NOT NULL;

-- Inserir eventos de entrega para pedidos entregues
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, ordem_evento)
SELECT 
    id as pedido_id,
    'pedido_entregue' as evento,
    'Pedido entregue com sucesso' as descricao,
    status,
    updated_at as data_evento,
    motoboy_id,
    3 as ordem_evento
FROM pedido 
WHERE status = 'entregue';

-- Verificar resultados
SELECT 'Timeline criada com sucesso!' as resultado;
SELECT COUNT(*) as total_eventos FROM timeline_pedido;
SELECT evento, COUNT(*) as quantidade FROM timeline_pedido GROUP BY evento ORDER BY evento;