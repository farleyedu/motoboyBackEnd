-- Script corrigido para timeline_pedido
-- Execute este script diretamente no PostgreSQL
-- Funciona com a estrutura atual da tabela

-- Verificar estrutura atual
SELECT 'Verificando estrutura atual da tabela timeline_pedido...' as status;

-- Limpar dados existentes se houver
DELETE FROM timeline_pedido;

-- Inserir dados iniciais baseados nos pedidos existentes
-- Evento 1: Pedido criado
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem, created_at)
SELECT 
    id as pedido_id,
    'pedido_criado' as evento,
    'Pedido criado no sistema' as descricao,
    1 as evento_ordem,
    created_at
FROM pedido;

-- Evento 2: Pedido atribuído (para pedidos que têm motoboy)
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem, created_at)
SELECT 
    id as pedido_id,
    'pedido_atribuido' as evento,
    'Pedido atribuído ao motoboy' as descricao,
    2 as evento_ordem,
    COALESCE(updated_at, created_at + INTERVAL '5 minutes') as created_at
FROM pedido 
WHERE motoboy_responsavel IS NOT NULL;

-- Evento 3: Em entrega (para pedidos em andamento)
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem, created_at)
SELECT 
    id as pedido_id,
    'em_entrega' as evento,
    'Motoboy a caminho do destino' as descricao,
    3 as evento_ordem,
    COALESCE(updated_at, created_at + INTERVAL '15 minutes') as created_at
FROM pedido 
WHERE motoboy_responsavel IS NOT NULL 
AND status_pedido IN (1, 2); -- Em andamento

-- Evento 4: Pedido entregue (para pedidos finalizados)
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem, created_at)
SELECT 
    id as pedido_id,
    'pedido_entregue' as evento,
    'Pedido entregue com sucesso' as descricao,
    4 as evento_ordem,
    COALESCE(updated_at, created_at + INTERVAL '30 minutes') as created_at
FROM pedido 
WHERE status_pedido = 3; -- Entregue

-- Evento para pedidos cancelados
INSERT INTO timeline_pedido (pedido_id, evento, descricao, evento_ordem, created_at)
SELECT 
    id as pedido_id,
    'pedido_cancelado' as evento,
    'Pedido cancelado' as descricao,
    CASE 
        WHEN motoboy_responsavel IS NOT NULL THEN 3
        ELSE 2
    END as evento_ordem,
    COALESCE(updated_at, created_at + INTERVAL '10 minutes') as created_at
FROM pedido 
WHERE status_pedido = 4; -- Cancelado

-- Verificar resultados
SELECT 'Timeline criada com sucesso!' as resultado;
SELECT COUNT(*) as total_eventos FROM timeline_pedido;
SELECT evento, COUNT(*) as quantidade FROM timeline_pedido GROUP BY evento ORDER BY evento;
SELECT pedido_id, COUNT(*) as eventos_por_pedido FROM timeline_pedido GROUP BY pedido_id ORDER BY pedido_id LIMIT 10;

-- Mostrar exemplo de timeline para o primeiro pedido
SELECT 'Timeline do Pedido 1:' as info;
SELECT * FROM timeline_pedido WHERE pedido_id = 1 ORDER BY evento_ordem;