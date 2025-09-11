-- Script para criar tabela itens_pedido
-- Execute este script diretamente no PostgreSQL

-- Verificar se a tabela já existe e tem as colunas corretas
SELECT 'Verificando estrutura atual da tabela itens_pedido...' as status;

-- Adicionar colunas que podem estar faltando na tabela itens_pedido
ALTER TABLE itens_pedido ADD COLUMN IF NOT EXISTS tipo VARCHAR(50) DEFAULT 'produto';
ALTER TABLE itens_pedido ADD COLUMN IF NOT EXISTS observacoes TEXT;
ALTER TABLE itens_pedido ADD COLUMN IF NOT EXISTS created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE itens_pedido ADD COLUMN IF NOT EXISTS updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP;

-- Criar índices para performance
CREATE INDEX IF NOT EXISTS idx_itens_pedido_pedido_id ON itens_pedido(pedido_id);
CREATE INDEX IF NOT EXISTS idx_itens_pedido_tipo ON itens_pedido(tipo);

-- Inserir alguns itens fictícios para os pedidos existentes
INSERT INTO itens_pedido (pedido_id, nome, quantidade, valor_unitario, valor_total, tipo, observacoes)
SELECT 
    p.id as pedido_id,
    CASE 
        WHEN p.id % 4 = 1 THEN 'Pizza Margherita'
        WHEN p.id % 4 = 2 THEN 'Hambúrguer Artesanal'
        WHEN p.id % 4 = 3 THEN 'Sushi Combo'
        ELSE 'Açaí 500ml'
    END as nome,
    CASE 
        WHEN p.id % 3 = 1 THEN 1
        WHEN p.id % 3 = 2 THEN 2
        ELSE 1
    END as quantidade,
    CASE 
        WHEN p.id % 4 = 1 THEN 35.90
        WHEN p.id % 4 = 2 THEN 28.50
        WHEN p.id % 4 = 3 THEN 45.00
        ELSE 18.90
    END as valor_unitario,
    CASE 
        WHEN p.id % 4 = 1 AND p.id % 3 = 1 THEN 35.90
        WHEN p.id % 4 = 1 AND p.id % 3 = 2 THEN 71.80
        WHEN p.id % 4 = 2 AND p.id % 3 = 1 THEN 28.50
        WHEN p.id % 4 = 2 AND p.id % 3 = 2 THEN 57.00
        WHEN p.id % 4 = 3 AND p.id % 3 = 1 THEN 45.00
        WHEN p.id % 4 = 3 AND p.id % 3 = 2 THEN 90.00
        WHEN p.id % 4 = 0 AND p.id % 3 = 1 THEN 18.90
        WHEN p.id % 4 = 0 AND p.id % 3 = 2 THEN 37.80
        ELSE 18.90
    END as valor_total,
    'produto' as tipo,
    CASE 
        WHEN p.id % 5 = 1 THEN 'Sem cebola'
        WHEN p.id % 5 = 2 THEN 'Extra molho'
        WHEN p.id % 5 = 3 THEN 'Bem passado'
        WHEN p.id % 5 = 4 THEN 'Sem wasabi'
        ELSE NULL
    END as observacoes
FROM pedido p
WHERE NOT EXISTS (
    SELECT 1 FROM itens_pedido ip WHERE ip.pedido_id = p.id
);

-- Adicionar um segundo item para alguns pedidos
INSERT INTO itens_pedido (pedido_id, nome, quantidade, valor_unitario, valor_total, tipo)
SELECT 
    p.id as pedido_id,
    CASE 
        WHEN p.id % 3 = 1 THEN 'Refrigerante 350ml'
        WHEN p.id % 3 = 2 THEN 'Batata Frita'
        ELSE 'Sobremesa do Dia'
    END as nome,
    1 as quantidade,
    CASE 
        WHEN p.id % 3 = 1 THEN 5.50
        WHEN p.id % 3 = 2 THEN 12.90
        ELSE 8.90
    END as valor_unitario,
    CASE 
        WHEN p.id % 3 = 1 THEN 5.50
        WHEN p.id % 3 = 2 THEN 12.90
        ELSE 8.90
    END as valor_total,
    'adicional' as tipo
FROM pedido p
WHERE p.id % 2 = 0  -- Apenas para pedidos pares
AND NOT EXISTS (
    SELECT 1 FROM itens_pedido ip 
    WHERE ip.pedido_id = p.id 
    AND ip.tipo = 'adicional'
);

-- Verificar resultados
SELECT 'Itens de pedido criados com sucesso!' as resultado;
SELECT COUNT(*) as total_itens FROM itens_pedido;
SELECT tipo, COUNT(*) as quantidade FROM itens_pedido GROUP BY tipo ORDER BY tipo;
SELECT pedido_id, COUNT(*) as itens_por_pedido FROM itens_pedido GROUP BY pedido_id ORDER BY pedido_id LIMIT 10;