-- =====================================================
-- SCRIPT DE DADOS FICTÍCIOS PARA ZIPPYGO
-- Dados realistas para teste de motoboys e pedidos
-- =====================================================

-- Limpar dados existentes (cuidado em produção!)
TRUNCATE TABLE pedido CASCADE;
TRUNCATE TABLE motoboy CASCADE;

-- =====================================================
-- INSERIR MOTOBOYS
-- =====================================================
INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES
(1, 'Carlos Silva', '(11) 98765-4321', 'carlos.silva@email.com', '123.456.789-01', '12345678901', 'ABC-1234', 'Honda CG 160', 'Vermelha', 'ativo', '2024-01-15 08:30:00', 'Rua das Flores, 123', 'Centro', 'São Paulo', '01234-567', 'Banco do Brasil', '1234-5', '12345-6', 'carlos.silva@pix.com', 'Motoboy experiente, pontual'),
(2, 'Maria Santos', '(11) 99876-5432', 'maria.santos@email.com', '987.654.321-02', '98765432102', 'XYZ-5678', 'Yamaha Factor 125', 'Azul', 'ativo', '2024-01-20 09:15:00', 'Av. Paulista, 456', 'Bela Vista', 'São Paulo', '01310-100', 'Itaú', '5678-9', '98765-4', 'maria.santos@pix.com', 'Conhece bem a região central'),
(3, 'João Oliveira', '(11) 97654-3210', 'joao.oliveira@email.com', '456.789.123-03', '45678912303', 'DEF-9012', 'Honda Biz 125', 'Preta', 'ativo', '2024-02-01 10:00:00', 'Rua Augusta, 789', 'Consolação', 'São Paulo', '01305-000', 'Caixa Econômica', '9012-3', '45678-9', '(11) 97654-3210', 'Especialista em entregas noturnas'),
(4, 'Ana Costa', '(11) 96543-2109', 'ana.costa@email.com', '789.123.456-04', '78912345604', 'GHI-3456', 'Yamaha XTZ 150', 'Branca', 'inativo', '2024-02-10 11:30:00', 'Rua Oscar Freire, 321', 'Jardins', 'São Paulo', '01426-001', 'Santander', '3456-7', '78912-3', 'ana.costa@pix.com', 'Temporariamente afastada'),
(5, 'Pedro Ferreira', '(11) 95432-1098', 'pedro.ferreira@email.com', '321.654.987-05', '32165498705', 'JKL-7890', 'Honda CG 160 Titan', 'Vermelha', 'ativo', '2024-02-15 14:20:00', 'Rua Haddock Lobo, 654', 'Cerqueira César', 'São Paulo', '01414-001', 'Bradesco', '7890-1', '32165-4', 'pedro.ferreira@pix.com', 'Rápido e eficiente');

-- =====================================================
-- INSERIR PEDIDOS COM DIFERENTES CENÁRIOS
-- =====================================================

-- Pedidos ENTREGUES (status 3)
INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, horario_pedido, data_pedido, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, items) VALUES
(1, 'IFOOD001', 'Roberto Almeida', '(11) 91234-5678', 'Rua Consolação, 1500 - Apto 45', 'Consolação', 'Centro', '-23.5505', '-46.6333', 45.90, 'pagoApp', '12:30:00', '2024-01-25 12:30:00', '12:45:00', '13:15:00', 3, 1, 'ZG001', '[{"nome": "Big Mac", "quantidade": 2, "precoTotal": 32.90, "observacoes": "Sem cebola"}, {"nome": "Coca-Cola 350ml", "quantidade": 2, "precoTotal": 13.00, "observacoes": ""}]'),
(2, 'IFOOD002', 'Fernanda Lima', '(11) 92345-6789', 'Av. Faria Lima, 2500 - Conj 12', 'Itaim Bibi', 'Sul', '-23.5751', '-46.6896', 67.50, 'dinheiro', '13:15:00', '2024-01-25 13:15:00', '13:30:00', '14:10:00', 3, 2, 'ZG002', '[{"nome": "Pizza Margherita Grande", "quantidade": 1, "precoTotal": 52.90, "observacoes": "Massa fina"}, {"nome": "Guaraná Antarctica 2L", "quantidade": 1, "precoTotal": 14.60, "observacoes": ""}]'),
(3, 'IFOOD003', 'Marcos Pereira', '(11) 93456-7890', 'Rua Oscar Freire, 800 - Casa', 'Jardins', 'Centro', '-23.5614', '-46.6707', 89.30, 'cartao', '19:45:00', '2024-01-25 19:45:00', '20:00:00', '20:35:00', 3, 3, 'ZG003', '[{"nome": "Hambúrguer Artesanal", "quantidade": 2, "precoTotal": 65.80, "observacoes": "Ponto da carne: mal passado"}, {"nome": "Batata Frita Grande", "quantidade": 1, "precoTotal": 18.50, "observacoes": "Extra crocante"}, {"nome": "Suco Natural Laranja", "quantidade": 1, "precoTotal": 5.00, "observacoes": ""}]'),

-- Pedidos EM ENTREGA (status 2)
(4, 'IFOOD004', 'Juliana Rocha', '(11) 94567-8901', 'Rua Augusta, 1200 - Apto 78', 'Consolação', 'Centro', '-23.5489', '-46.6388', 34.70, 'pagoApp', '14:20:00', '2024-01-26 14:20:00', '14:35:00', NULL, 2, 1, 'ZG004', '[{"nome": "Açaí 500ml", "quantidade": 1, "precoTotal": 22.90, "observacoes": "Com granola e banana"}, {"nome": "Água Mineral 500ml", "quantidade": 1, "precoTotal": 11.80, "observacoes": ""}]'),
(5, 'IFOOD005', 'Ricardo Santos', '(11) 95678-9012', 'Av. Paulista, 1000 - 15º andar', 'Bela Vista', 'Centro', '-23.5618', '-46.6565', 78.20, 'dinheiro', '15:10:00', '2024-01-26 15:10:00', '15:25:00', NULL, 2, 2, 'ZG005', '[{"nome": "Sushi Combo 20 peças", "quantidade": 1, "precoTotal": 65.90, "observacoes": "Sem wasabi"}, {"nome": "Chá Verde", "quantidade": 1, "precoTotal": 12.30, "observacoes": ""}]'),

-- Pedidos ATRIBUÍDOS (status 1)
(6, 'IFOOD006', 'Camila Souza', '(11) 96789-0123', 'Rua Haddock Lobo, 300 - Apto 12', 'Cerqueira César', 'Centro', '-23.5567', '-46.6634', 56.40, 'cartao', '16:30:00', '2024-01-26 16:30:00', NULL, NULL, 1, 5, 'ZG006', '[{"nome": "Lasanha Bolonhesa", "quantidade": 1, "precoTotal": 38.90, "observacoes": "Bem quente"}, {"nome": "Salada Caesar", "quantidade": 1, "precoTotal": 17.50, "observacoes": "Molho à parte"}]'),
(7, 'IFOOD007', 'Daniel Costa', '(11) 97890-1234', 'Alameda Santos, 2500 - Cobertura', 'Jardins', 'Centro', '-23.5656', '-46.6734', 125.80, 'pagoApp', '17:00:00', '2024-01-26 17:00:00', NULL, NULL, 1, 3, 'ZG007', '[{"nome": "Picanha Grelhada 400g", "quantidade": 1, "precoTotal": 89.90, "observacoes": "Ponto: ao ponto"}, {"nome": "Arroz Branco", "quantidade": 1, "precoTotal": 8.90, "observacoes": ""}, {"nome": "Farofa", "quantidade": 1, "precoTotal": 12.00, "observacoes": ""}, {"nome": "Cerveja Heineken 600ml", "quantidade": 2, "precoTotal": 15.00, "observacoes": "Bem gelada"}]'),

-- Pedidos PENDENTES (status 0)
(8, 'IFOOD008', 'Luciana Martins', '(11) 98901-2345', 'Rua da Consolação, 800 - Loja 5', 'República', 'Centro', '-23.5434', '-46.6467', 42.30, 'dinheiro', '18:15:00', '2024-01-26 18:15:00', NULL, NULL, 0, NULL, 'ZG008', '[{"nome": "Sanduíche Natural", "quantidade": 2, "precoTotal": 28.80, "observacoes": "Sem maionese"}, {"nome": "Suco Detox", "quantidade": 2, "precoTotal": 13.50, "observacoes": "Com couve e limão"}]'),
(9, 'IFOOD009', 'Thiago Oliveira', '(11) 99012-3456', 'Av. Rebouças, 1500 - Apto 203', 'Pinheiros', 'Oeste', '-23.5689', '-46.6934', 73.90, 'cartao', '18:45:00', '2024-01-26 18:45:00', NULL, NULL, 0, NULL, 'ZG009', '[{"nome": "Pizza Portuguesa Família", "quantidade": 1, "precoTotal": 58.90, "observacoes": "Borda recheada"}, {"nome": "Refrigerante 2L", "quantidade": 1, "precoTotal": 15.00, "observacoes": "Coca-Cola"}]'),
(10, 'IFOOD010', 'Patrícia Silva', '(11) 90123-4567', 'Rua Teodoro Sampaio, 1200 - Casa', 'Pinheiros', 'Oeste', '-23.5712', '-46.6923', 95.60, 'pagoApp', '19:20:00', '2024-01-26 19:20:00', NULL, NULL, 0, NULL, 'ZG010', '[{"nome": "Combo Japonês 30 peças", "quantidade": 1, "precoTotal": 78.90, "observacoes": "Variado"}, {"nome": "Temaki Salmão", "quantidade": 2, "precoTotal": 16.70, "observacoes": "Sem pepino"}]'),

-- Pedidos CANCELADOS (status -1)
(11, 'IFOOD011', 'Bruno Ferreira', '(11) 91234-5670', 'Rua Bela Cintra, 500 - Apto 89', 'Consolação', 'Centro', '-23.5523', '-46.6445', 38.50, 'dinheiro', '20:00:00', '2024-01-26 20:00:00', NULL, NULL, -1, NULL, 'ZG011', '[{"nome": "Hambúrguer Simples", "quantidade": 1, "precoTotal": 25.90, "observacoes": ""}, {"nome": "Batata Frita Média", "quantidade": 1, "precoTotal": 12.60, "observacoes": ""}]'),
(12, 'IFOOD012', 'Carla Mendes', '(11) 92345-6781', 'Av. Ipiranga, 900 - Sala 12', 'República', 'Centro', '-23.5456', '-46.6389', 29.80, 'cartao', '20:30:00', '2024-01-26 20:30:00', NULL, NULL, -1, NULL, 'ZG012', '[{"nome": "Salada Fitness", "quantidade": 1, "precoTotal": 22.90, "observacoes": "Molho à parte"}, {"nome": "Água com Gás", "quantidade": 1, "precoTotal": 6.90, "observacoes": ""}]');

-- =====================================================
-- ATUALIZAR SEQUENCES (se necessário)
-- =====================================================
SELECT setval('motoboy_id_seq', (SELECT MAX(id) FROM motoboy));
SELECT setval('pedido_id_seq', (SELECT MAX(id) FROM pedido));

-- =====================================================
-- VERIFICAR DADOS INSERIDOS
-- =====================================================
SELECT 'MOTOBOYS INSERIDOS:' as info, COUNT(*) as total FROM motoboy;
SELECT 'PEDIDOS INSERIDOS:' as info, COUNT(*) as total FROM pedido;
SELECT 'PEDIDOS POR STATUS:' as info, status_pedido, COUNT(*) as total FROM pedido GROUP BY status_pedido ORDER BY status_pedido;

-- =====================================================
-- CONSULTAS DE TESTE
-- =====================================================
-- Pedidos com motoboy
SELECT p.id, p.nome_cliente, p.status_pedido, m.nome as motoboy 
FROM pedido p 
LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel 
ORDER BY p.id;

-- Resumo por status
SELECT 
    CASE 
        WHEN status_pedido = -1 THEN 'Cancelado'
        WHEN status_pedido = 0 THEN 'Pendente'
        WHEN status_pedido = 1 THEN 'Atribuído'
        WHEN status_pedido = 2 THEN 'Em Entrega'
        WHEN status_pedido = 3 THEN 'Entregue'
        ELSE 'Desconhecido'
    END as status_nome,
    COUNT(*) as quantidade,
    SUM(value) as valor_total
FROM pedido 
GROUP BY status_pedido 
ORDER BY status_pedido;

PRINT '✅ Dados fictícios inseridos com sucesso!';
PRINT '📊 12 pedidos criados com diferentes status e cenários';
PRINT '🏍️ 5 motoboys cadastrados (4 ativos, 1 inativo)';
PRINT '🎯 Pronto para testar os endpoints!';