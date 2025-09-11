-- =====================================================
-- SCRIPT DE RESTAURAÇÃO COMPLETA DE DADOS - ZippyGo
-- =====================================================
-- Execute este script para restaurar todos os dados
-- Farley, cole este script diretamente no seu banco!

-- Limpar dados existentes (se houver)
TRUNCATE TABLE pedido RESTART IDENTITY CASCADE;
TRUNCATE TABLE motoboy RESTART IDENTITY CASCADE;

-- =====================================================
-- INSERIR MOTOBOYS
-- =====================================================
INSERT INTO motoboy (nome, telefone, cnh, placa_moto, marca_moto, modelo_moto, status, qtd_pedidos_ativos, latitude, longitude) VALUES
('João Silva', '(11) 99999-1111', '12345678901', 'ABC-1234', 'Honda', 'CG 160', 1, 0, '-23.5505', '-46.6333'),
('Maria Santos', '(11) 99999-2222', '12345678902', 'DEF-5678', 'Yamaha', 'Factor 125', 1, 0, '-23.5489', '-46.6388'),
('Pedro Oliveira', '(11) 99999-3333', '12345678903', 'GHI-9012', 'Honda', 'Biz 125', 1, 0, '-23.5629', '-46.6544'),
('Ana Costa', '(11) 99999-4444', '12345678904', 'JKL-3456', 'Suzuki', 'Intruder 125', 2, 1, '-23.5475', '-46.6361'),
('Carlos Ferreira', '(11) 99999-5555', '12345678905', 'MNO-7890', 'Honda', 'CB 600F', 1, 0, '-23.5505', '-46.6333'),
('Lucia Mendes', '(11) 99999-6666', '12345678906', 'PQR-1234', 'Yamaha', 'XTZ 250', 1, 0, '-23.5505', '-46.6333'),
('Roberto Lima', '(11) 99999-7777', '12345678907', 'STU-5678', 'Honda', 'PCX 150', 3, 0, '-23.5505', '-46.6333'),
('Fernanda Rocha', '(11) 99999-8888', '12345678908', 'VWX-9012', 'Suzuki', 'Burgman 125', 1, 0, '-23.5505', '-46.6333');

-- =====================================================
-- INSERIR PEDIDOS VARIADOS
-- =====================================================
INSERT INTO pedido (nome_cliente, endereco_entrega, telefone_cliente, data_pedido, status_pedido, motoboy_responsavel, horario_entrega, items, value, region, latitude, longitude, horario_pedido, previsao_entrega, entrega_rua, entrega_numero, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep, tipo_pagamento, distancia_km, observacoes, troco, status_pagamento) VALUES
-- Pedidos ENTREGUES (status 4)
('Cliente A', 'Av. Paulista, 500 - Bela Vista', '(11) 98888-1111', '2024-01-15', 4, 1, NOW() - INTERVAL '30 minutes', 'Pizza Margherita, Refrigerante 2L', 45.50, 'Centro', '-23.5505', '-46.6333', '18:30', '19:30', 'Av. Paulista', '500', 'Bela Vista', 'São Paulo', 'SP', '01310-100', 'Cartão', 2.5, 'Entrega realizada com sucesso', 0, 'Pago'),
('Cliente B', 'Rua da Consolação, 300 - Centro', '(11) 98888-2222', '2024-01-15', 4, 2, NOW() - INTERVAL '1 hour', 'Hambúrguer Artesanal, Batata Frita', 32.00, 'Centro', '-23.5489', '-46.6388', '17:45', '18:45', 'Rua da Consolação', '300', 'Centro', 'São Paulo', 'SP', '01302-000', 'PIX', 1.8, 'Cliente satisfeito', 0, 'Pago'),
('Cliente C', 'Rua Haddock Lobo, 100 - Cerqueira César', '(11) 98888-3333', '2024-01-15', 4, 3, NOW() - INTERVAL '2 hours', 'Sushi Combo, Temaki Salmão', 68.75, 'Jardins', '-23.5629', '-46.6544', '16:00', '17:00', 'Rua Haddock Lobo', '100', 'Cerqueira César', 'São Paulo', 'SP', '01414-000', 'Dinheiro', 3.2, 'Entrega no prazo', 10.00, 'Pago'),
('Cliente D', 'Av. Rebouças, 600 - Pinheiros', '(11) 98888-4444', '2024-01-15', 4, 1, NOW() - INTERVAL '3 hours', 'Açaí 500ml, Granola', 18.50, 'Pinheiros', '-23.5475', '-46.6361', '15:30', '16:30', 'Av. Rebouças', '600', 'Pinheiros', 'São Paulo', 'SP', '05402-000', 'Cartão', 1.5, 'Sem intercorrências', 0, 'Pago'),

-- Pedidos EM ENTREGA (status 3)
('Cliente E', 'Rua Henrique Schaumann, 200 - Cerqueira César', '(11) 98888-5555', '2024-01-15', 3, 2, NULL, 'Marmitex Executiva, Suco Natural', 25.90, 'Jardins', '-23.5505', '-46.6333', '19:15', '20:15', 'Rua Henrique Schaumann', '200', 'Cerqueira César', 'São Paulo', 'SP', '01413-000', 'PIX', 2.1, 'Motoboy a caminho do destino', 0, 'Pendente'),
('Cliente F', 'Rua Bela Cintra, 300 - Consolação', '(11) 98888-6666', '2024-01-15', 3, 5, NULL, 'Pizza Calabresa, Coca-Cola 1L', 38.00, 'Centro', '-23.5489', '-46.6388', '19:30', '20:30', 'Rua Bela Cintra', '300', 'Consolação', 'São Paulo', 'SP', '01415-000', 'Cartão', 1.9, 'Entrega em andamento', 0, 'Pendente'),
('Cliente G', 'Av. Ibirapuera, 800 - Moema', '(11) 98888-7777', '2024-01-15', 3, 6, NULL, 'Poke Bowl, Água com Gás', 42.50, 'Moema', '-23.5505', '-46.6333', '19:40', '20:40', 'Av. Ibirapuera', '800', 'Moema', 'São Paulo', 'SP', '04029-000', 'Dinheiro', 3.8, 'Trânsito intenso, mas dentro do prazo', 5.00, 'Pendente'),

-- Pedidos ATRIBUÍDOS (status 2)
('Cliente H', 'Rua Gomes de Carvalho, 100 - Vila Olímpia', '(11) 98888-8888', '2024-01-15', 2, 8, NULL, 'Sanduíche Natural, Suco Detox', 22.00, 'Vila Olímpia', '-23.5505', '-46.6333', '19:45', '20:45', 'Rua Gomes de Carvalho', '100', 'Vila Olímpia', 'São Paulo', 'SP', '04547-000', 'PIX', 1.2, 'Motoboy confirmou retirada', 0, 'Pendente'),
('Cliente I', 'Rua Leopoldo Couto Magalhães Jr, 50 - Itaim Bibi', '(11) 98888-9999', '2024-01-15', 2, 1, NULL, 'Salada Caesar, Água', 28.90, 'Itaim Bibi', '-23.5505', '-46.6333', '19:50', '20:50', 'Rua Leopoldo Couto Magalhães Jr', '50', 'Itaim Bibi', 'São Paulo', 'SP', '04542-000', 'Cartão', 0.8, 'Aguardando retirada', 0, 'Pendente'),
('Cliente J', 'Av. Santo Amaro, 1000 - Brooklin', '(11) 98888-0000', '2024-01-15', 2, 3, NULL, 'Churrasco Completo, Farofa', 55.00, 'Brooklin', '-23.5505', '-46.6333', '19:52', '20:52', 'Av. Santo Amaro', '1000', 'Brooklin', 'São Paulo', 'SP', '04506-000', 'Dinheiro', 4.5, 'Pedido confirmado pelo motoboy', 15.00, 'Pendente'),

-- Pedidos PENDENTES (status 1)
('Cliente K', 'Rua Domingos de Morais, 500 - Vila Mariana', '(11) 97777-1111', '2024-01-15', 1, NULL, NULL, 'Pastel Assado, Caldo de Cana', 15.50, 'Vila Mariana', '-23.5505', '-46.6333', '19:55', '20:55', 'Rua Domingos de Morais', '500', 'Vila Mariana', 'São Paulo', 'SP', '04010-000', 'PIX', 2.8, 'Aguardando motoboy disponível', 0, 'Pendente'),
('Cliente L', 'Rua 25 de Março, 200 - Centro', '(11) 97777-2222', '2024-01-15', 1, NULL, NULL, 'Coxinha, Guaraná', 12.00, 'Centro', '-23.5505', '-46.6333', '19:57', '20:57', 'Rua 25 de Março', '200', 'Centro', 'São Paulo', 'SP', '01021-000', 'Dinheiro', 3.5, 'Pedido urgente', 3.00, 'Pendente'),
('Cliente M', 'Rua Estados Unidos, 300 - Jardins', '(11) 97777-3333', '2024-01-15', 1, NULL, NULL, 'Crepe Doce, Café Expresso', 18.90, 'Jardins', '-23.5505', '-46.6333', '19:58', '20:58', 'Rua Estados Unidos', '300', 'Jardins', 'São Paulo', 'SP', '01427-000', 'Cartão', 1.1, 'Entrega para hoje', 0, 'Pendente'),
('Cliente N', 'Rua da Consolação, 800 - Higienópolis', '(11) 97777-4444', '2024-01-15', 1, NULL, NULL, 'Risotto de Camarão, Vinho', 85.00, 'Higienópolis', '-23.5505', '-46.6333', '19:59', '20:59', 'Rua da Consolação', '800', 'Higienópolis', 'São Paulo', 'SP', '01302-000', 'PIX', 2.2, 'Cliente VIP', 0, 'Pendente'),

-- Pedidos CANCELADOS (status 5)
('Cliente O', 'Av. Angélica, 200 - Higienópolis', '(11) 97777-5555', '2024-01-15', 5, NULL, NULL, 'Pizza Portuguesa, Refrigerante', 35.75, 'Higienópolis', '-23.5505', '-46.6333', '13:00', '14:00', 'Av. Angélica', '200', 'Higienópolis', 'São Paulo', 'SP', '01227-000', 'Cartão', 1.8, 'Cancelado pelo cliente', 0, 'Cancelado'),
('Cliente P', 'Rua Haddock Lobo, 400 - Cerqueira César', '(11) 97777-6666', '2024-01-15', 5, NULL, NULL, 'Hambúrguer Vegano, Suco Verde', 29.50, 'Jardins', '-23.5505', '-46.6333', '12:00', '13:00', 'Rua Haddock Lobo', '400', 'Cerqueira César', 'São Paulo', 'SP', '01414-000', 'PIX', 2.1, 'Produto indisponível', 0, 'Cancelado');

-- =====================================================
-- ATUALIZAR SEQUÊNCIAS
-- =====================================================
SELECT setval('motoboy_id_seq', (SELECT MAX(id) FROM motoboy));
SELECT setval('pedido_id_seq', (SELECT MAX(id) FROM pedido));

-- =====================================================
-- VERIFICAÇÃO DOS DADOS INSERIDOS
-- =====================================================
SELECT 'MOTOBOYS INSERIDOS:' as info, COUNT(*) as total FROM motoboy;
SELECT 'PEDIDOS INSERIDOS:' as info, COUNT(*) as total FROM pedido;

-- Distribuição por status de pedidos
SELECT 
    CASE status_pedido
        WHEN 1 THEN 'PENDENTES'
        WHEN 2 THEN 'ATRIBUÍDOS'
        WHEN 3 THEN 'EM ENTREGA'
        WHEN 4 THEN 'ENTREGUES'
        WHEN 5 THEN 'CANCELADOS'
    END as status,
    COUNT(*) as quantidade
FROM pedido 
GROUP BY status_pedido 
ORDER BY status_pedido;

-- Motoboys por status
SELECT 
    CASE status
        WHEN 1 THEN 'DISPONÍVEL'
        WHEN 2 THEN 'OCUPADO'
        WHEN 3 THEN 'OFFLINE'
    END as status,
    COUNT(*) as quantidade
FROM motoboy 
GROUP BY status 
ORDER BY status;

-- =====================================================
-- DADOS RESTAURADOS COM SUCESSO!
-- =====================================================
-- Total: 8 motoboys + 16 pedidos
-- Pronto para testar os endpoints da API!