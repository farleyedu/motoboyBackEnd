-- Script para criar tabelas timeline e itens_pedido
-- ZippyGo - Estrutura normalizada para itens e eventos

-- Tabela para itens do pedido (normalizada)
CREATE TABLE IF NOT EXISTS itens_pedido (
    id BIGSERIAL PRIMARY KEY,
    pedido_id BIGINT NOT NULL REFERENCES pedido(id) ON DELETE CASCADE,
    nome VARCHAR(200) NOT NULL,
    quantidade INTEGER NOT NULL DEFAULT 1,
    valor_unitario DECIMAL(10,2) NOT NULL DEFAULT 0.00,
    valor_total DECIMAL(10,2) NOT NULL DEFAULT 0.00,
    tipo VARCHAR(50) NOT NULL DEFAULT 'comida', -- 'comida', 'bebida', 'sobremesa', 'outros'
    observacoes TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Tabela para timeline de eventos do pedido
CREATE TABLE IF NOT EXISTS timeline_pedido (
    id BIGSERIAL PRIMARY KEY,
    pedido_id BIGINT NOT NULL REFERENCES pedido(id) ON DELETE CASCADE,
    evento VARCHAR(100) NOT NULL,
    descricao TEXT,
    status VARCHAR(50) NOT NULL, -- 'pendente', 'em_andamento', 'concluido', 'cancelado'
    data_evento TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    motoboy_id BIGINT REFERENCES motoboy(id),
    localizacao VARCHAR(500),
    observacoes TEXT,
    ordem_evento INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Índices para performance
CREATE INDEX IF NOT EXISTS idx_itens_pedido_pedido_id ON itens_pedido(pedido_id);
CREATE INDEX IF NOT EXISTS idx_itens_pedido_tipo ON itens_pedido(tipo);

CREATE INDEX IF NOT EXISTS idx_timeline_pedido_pedido_id ON timeline_pedido(pedido_id);
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_status ON timeline_pedido(status);
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_data_evento ON timeline_pedido(data_evento);
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_motoboy_id ON timeline_pedido(motoboy_id);
CREATE INDEX IF NOT EXISTS idx_timeline_pedido_ordem ON timeline_pedido(pedido_id, ordem_evento);

-- Inserir itens dos pedidos existentes (parseando o campo items)
INSERT INTO itens_pedido (pedido_id, nome, quantidade, valor_unitario, valor_total, tipo)
SELECT 
    p.id as pedido_id,
    TRIM(item_name) as nome,
    1 as quantidade,
    ROUND(p.value / GREATEST(array_length(string_to_array(p.items, ','), 1), 1), 2) as valor_unitario,
    ROUND(p.value / GREATEST(array_length(string_to_array(p.items, ','), 1), 1), 2) as valor_total,
    CASE 
        WHEN LOWER(TRIM(item_name)) LIKE '%bebida%' OR LOWER(TRIM(item_name)) LIKE '%refrigerante%' 
             OR LOWER(TRIM(item_name)) LIKE '%suco%' OR LOWER(TRIM(item_name)) LIKE '%água%'
             OR LOWER(TRIM(item_name)) LIKE '%coca%' OR LOWER(TRIM(item_name)) LIKE '%guaraná%'
             OR LOWER(TRIM(item_name)) LIKE '%cerveja%' OR LOWER(TRIM(item_name)) LIKE '%vinho%'
        THEN 'bebida'
        WHEN LOWER(TRIM(item_name)) LIKE '%sobremesa%' OR LOWER(TRIM(item_name)) LIKE '%doce%'
             OR LOWER(TRIM(item_name)) LIKE '%pudim%' OR LOWER(TRIM(item_name)) LIKE '%torta%'
             OR LOWER(TRIM(item_name)) LIKE '%sorvete%' OR LOWER(TRIM(item_name)) LIKE '%açaí%'
        THEN 'sobremesa'
        ELSE 'comida'
    END as tipo
FROM pedido p
CROSS JOIN LATERAL unnest(string_to_array(p.items, ',')) AS item_name
WHERE p.items IS NOT NULL AND p.items != '';

-- Inserir timeline básica para pedidos existentes
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, localizacao, ordem_evento)
SELECT 
    p.id as pedido_id,
    'Pedido Criado' as evento,
    'Pedido recebido e registrado no sistema' as descricao,
    'concluido' as status,
    p.created_at as data_evento,
    NULL as motoboy_id,
    COALESCE(p.endereco_entrega, 'Endereço não informado') as localizacao,
    1 as ordem_evento
FROM pedido p;

-- Timeline para pedidos atribuídos
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, localizacao, ordem_evento)
SELECT 
    p.id as pedido_id,
    'Atribuído ao Motoboy' as evento,
    'Pedido atribuído para entrega' as descricao,
    CASE 
        WHEN p.status_pedido IN ('entregue', 'cancelado') THEN 'concluido'
        WHEN p.status_pedido IN ('em_entrega', 'a_caminho') THEN 'concluido'
        ELSE 'pendente'
    END as status,
    p.created_at + INTERVAL '5 minutes' as data_evento,
    p.motoboy_id,
    COALESCE(m.nome, 'Motoboy não identificado') as localizacao,
    2 as ordem_evento
FROM pedido p
LEFT JOIN motoboy m ON p.motoboy_id = m.id
WHERE p.motoboy_id IS NOT NULL;

-- Timeline para pedidos em entrega
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, localizacao, ordem_evento)
SELECT 
    p.id as pedido_id,
    'Em Entrega' as evento,
    'Motoboy a caminho do destino' as descricao,
    CASE 
        WHEN p.status_pedido IN ('entregue', 'cancelado') THEN 'concluido'
        WHEN p.status_pedido = 'em_entrega' THEN 'em_andamento'
        ELSE 'pendente'
    END as status,
    p.created_at + INTERVAL '15 minutes' as data_evento,
    p.motoboy_id,
    'Em trânsito' as localizacao,
    3 as ordem_evento
FROM pedido p
WHERE p.motoboy_id IS NOT NULL AND p.status_pedido IN ('em_entrega', 'a_caminho', 'entregue');

-- Timeline para pedidos entregues
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, localizacao, ordem_evento)
SELECT 
    p.id as pedido_id,
    'Entregue' as evento,
    'Pedido entregue com sucesso' as descricao,
    CASE 
        WHEN p.status_pedido = 'entregue' THEN 'concluido'
        ELSE 'pendente'
    END as status,
    p.created_at + INTERVAL '30 minutes' as data_evento,
    p.motoboy_id,
    COALESCE(p.endereco_entrega, 'Local de entrega') as localizacao,
    4 as ordem_evento
FROM pedido p
WHERE p.status_pedido = 'entregue';

-- Timeline para pedidos cancelados
INSERT INTO timeline_pedido (pedido_id, evento, descricao, status, data_evento, motoboy_id, localizacao, ordem_evento)
SELECT 
    p.id as pedido_id,
    'Cancelado' as evento,
    'Pedido cancelado' as descricao,
    'cancelado' as status,
    p.created_at + INTERVAL '10 minutes' as data_evento,
    p.motoboy_id,
    'Sistema' as localizacao,
    CASE 
        WHEN p.motoboy_id IS NOT NULL THEN 3
        ELSE 2
    END as ordem_evento
FROM pedido p
WHERE p.status_pedido = 'cancelado';

-- Verificar dados inseridos
SELECT 'Itens inseridos:' as info, COUNT(*) as total FROM itens_pedido;
SELECT 'Timeline inserida:' as info, COUNT(*) as total FROM timeline_pedido;

-- Mostrar exemplo de dados
SELECT 'Exemplo - Itens do Pedido 1:' as info;
SELECT * FROM itens_pedido WHERE pedido_id = 1 ORDER BY id;

SELECT 'Exemplo - Timeline do Pedido 1:' as info;
SELECT * FROM timeline_pedido WHERE pedido_id = 1 ORDER BY ordem_evento;

COMMIT;