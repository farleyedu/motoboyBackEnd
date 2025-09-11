using Npgsql;
using System.Text.Json;

namespace DataSeeder
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var connectionString = "Host=hopper.proxy.rlwy.net;Port=51122;Database=railway;Username=postgres;Password=KvBJrxnTRewIOfOkvMIZdJBLUQoROmpR;SSL Mode=Require;Trust Server Certificate=true;";
            
            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                Console.WriteLine("🔗 Conectado ao PostgreSQL");
                Console.WriteLine("🗑️ Limpando dados existentes...");
                
                // Limpar dados existentes
                await ExecuteCommand(connection, "TRUNCATE TABLE pedido CASCADE;");
                await ExecuteCommand(connection, "TRUNCATE TABLE motoboy CASCADE;");
                
                Console.WriteLine("🏍️ Inserindo motoboys...");
                
                // Inserir motoboys
                var motoboyInserts = new[]
                {
                    "INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES (1, 'Carlos Silva', '(11) 98765-4321', 'carlos.silva@email.com', '123.456.789-01', '12345678901', 'ABC-1234', 'Honda CG 160', 'Vermelha', 'ativo', '2024-01-15 08:30:00', 'Rua das Flores, 123', 'Centro', 'São Paulo', '01234-567', 'Banco do Brasil', '1234-5', '12345-6', 'carlos.silva@pix.com', 'Motoboy experiente, pontual');",
                    "INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES (2, 'Maria Santos', '(11) 99876-5432', 'maria.santos@email.com', '987.654.321-02', '98765432102', 'XYZ-5678', 'Yamaha Factor 125', 'Azul', 'ativo', '2024-01-20 09:15:00', 'Av. Paulista, 456', 'Bela Vista', 'São Paulo', '01310-100', 'Itaú', '5678-9', '98765-4', 'maria.santos@pix.com', 'Conhece bem a região central');",
                    "INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES (3, 'João Pereira', '(11) 97654-3210', 'joao.pereira@email.com', '456.789.123-03', '45678912303', 'DEF-9012', 'Honda Biz 125', 'Preta', 'ativo', '2024-02-01 10:00:00', 'Rua Augusta, 789', 'Consolação', 'São Paulo', '01305-000', 'Bradesco', '9012-3', '54321-0', 'joao.pereira@pix.com', 'Rápido nas entregas');",
                    "INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES (4, 'Ana Costa', '(11) 96543-2109', 'ana.costa@email.com', '789.123.456-04', '78912345604', 'GHI-3456', 'Yamaha XTZ 150', 'Branca', 'ativo', '2024-02-10 11:30:00', 'Rua Oscar Freire, 321', 'Jardins', 'São Paulo', '01426-001', 'Santander', '3456-7', '09876-5', 'ana.costa@pix.com', 'Cuidadosa com os pedidos');",
                    "INSERT INTO motoboy (id, nome, telefone, email, cpf, cnh, placa_moto, modelo_moto, cor_moto, status, data_cadastro, endereco, bairro, cidade, cep, banco, agencia, conta, pix, observacoes) VALUES (5, 'Roberto Lima', '(11) 95432-1098', 'roberto.lima@email.com', '321.654.987-05', '32165498705', 'JKL-7890', 'Honda CG 150', 'Azul', 'inativo', '2024-01-05 07:45:00', 'Rua da Consolação, 654', 'Centro', 'São Paulo', '01302-907', 'Caixa', '7890-1', '13579-2', 'roberto.lima@pix.com', 'Temporariamente inativo');"
                };
                
                foreach (var insert in motoboyInserts)
                {
                    await ExecuteCommand(connection, insert);
                }
                
                Console.WriteLine("📦 Inserindo pedidos...");
                
                // Inserir pedidos
                var pedidoInserts = new[]
                {
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (1, 'IFOOD001', 'João Silva', '(11) 99999-1111', 'Rua A, 123 - Apto 45', 'Vila Olímpia', 'Sul', '-23.5955', '-46.6890', 45.90, 'pix', '[{\"nome\": \"Hambúrguer Artesanal\", \"quantidade\": 1, \"preco\": 28.90}, {\"nome\": \"Batata Frita\", \"quantidade\": 1, \"preco\": 12.00}, {\"nome\": \"Refrigerante\", \"quantidade\": 1, \"preco\": 5.00}]', '2024-12-20', '18:30:00', '2024-12-20 19:30:00', '2024-12-20 18:45:00', '2024-12-20 19:15:00', 3, 1, 'ENT001', 'Entregue no portão');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (2, 'IFOOD002', 'Maria Oliveira', '(11) 88888-2222', 'Av. Paulista, 1000 - Conj 12', 'Bela Vista', 'Centro', '-23.5618', '-46.6565', 32.50, 'cartao', '[{\"nome\": \"Pizza Margherita\", \"quantidade\": 1, \"preco\": 32.50}]', '2024-12-20', '19:00:00', '2024-12-20 20:00:00', '2024-12-20 19:15:00', '2024-12-20 19:45:00', 3, 2, 'ENT002', 'Cliente satisfeito');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (3, 'IFOOD003', 'Pedro Costa', '(11) 77777-3333', 'Rua Oscar Freire, 500', 'Jardins', 'Oeste', '-23.5677', '-46.6729', 67.80, 'cartao', '[{\"nome\": \"Sushi Combo\", \"quantidade\": 1, \"preco\": 55.00}, {\"nome\": \"Temaki\", \"quantidade\": 2, \"preco\": 12.80}]', '2024-12-20', '20:15:00', '2024-12-20 21:15:00', '2024-12-20 20:30:00', '2024-12-20 21:00:00', 3, 3, 'ENT003', 'Entrega rápida');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (4, 'IFOOD004', 'Ana Souza', '(11) 66666-4444', 'Rua Haddock Lobo, 200', 'Cerqueira César', 'Centro', '-23.5689', '-46.6653', 28.90, 'dinheiro', '[{\"nome\": \"Açaí 500ml\", \"quantidade\": 1, \"preco\": 18.90}, {\"nome\": \"Granola\", \"quantidade\": 1, \"preco\": 5.00}, {\"nome\": \"Banana\", \"quantidade\": 1, \"preco\": 5.00}]', '2024-12-20', '16:45:00', '2024-12-20 17:30:00', '2024-12-20 17:00:00', NULL, 2, 1, 'ROTA004', 'Saiu para entrega');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (5, 'IFOOD005', 'Carlos Mendes', '(11) 55555-5555', 'Av. Faria Lima, 1500', 'Itaim Bibi', 'Sul', '-23.5847', '-46.6869', 42.30, 'pix', '[{\"nome\": \"Poke Bowl\", \"quantidade\": 1, \"preco\": 35.30}, {\"nome\": \"Água\", \"quantidade\": 1, \"preco\": 7.00}]', '2024-12-20', '12:30:00', '2024-12-20 13:30:00', '2024-12-20 12:45:00', NULL, 2, 2, 'ROTA005', 'A caminho do cliente');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (6, 'IFOOD006', 'Fernanda Lima', '(11) 44444-6666', 'Rua Teodoro Sampaio, 800', 'Pinheiros', 'Oeste', '-23.5644', '-46.6892', 38.70, 'cartao', '[{\"nome\": \"Lasanha\", \"quantidade\": 1, \"preco\": 25.70}, {\"nome\": \"Salada\", \"quantidade\": 1, \"preco\": 8.00}, {\"nome\": \"Suco Natural\", \"quantidade\": 1, \"preco\": 5.00}]', '2024-12-20', '19:45:00', '2024-12-20 20:45:00', '2024-12-20 20:00:00', NULL, 1, 3, 'ATRIB006', 'Atribuído ao motoboy');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (7, 'IFOOD007', 'Ricardo Santos', '(11) 33333-7777', 'Rua da Consolação, 1200', 'Consolação', 'Centro', '-23.5567', '-46.6598', 51.20, 'pix', '[{\"nome\": \"Churrasco\", \"quantidade\": 1, \"preco\": 45.20}, {\"nome\": \"Farofa\", \"quantidade\": 1, \"preco\": 6.00}]', '2024-12-20', '13:15:00', '2024-12-20 14:15:00', NULL, NULL, 1, 4, 'ATRIB007', 'Aguardando saída');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (8, 'IFOOD008', 'Juliana Rocha', '(11) 22222-8888', 'Av. Rebouças, 600', 'Pinheiros', 'Oeste', '-23.5678', '-46.6789', 29.80, 'dinheiro', '[{\"nome\": \"Sanduíche Natural\", \"quantidade\": 2, \"preco\": 24.80}, {\"nome\": \"Suco Detox\", \"quantidade\": 1, \"preco\": 5.00}]', '2024-12-20', '11:00:00', '2024-12-20 12:00:00', NULL, NULL, 0, NULL, 'PEND008', 'Aguardando motoboy');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (9, 'IFOOD009', 'Marcos Alves', '(11) 11111-9999', 'Rua Bela Cintra, 300', 'Consolação', 'Centro', '-23.5589', '-46.6612', 35.60, 'cartao', '[{\"nome\": \"Hambúrguer Vegano\", \"quantidade\": 1, \"preco\": 22.60}, {\"nome\": \"Batata Doce\", \"quantidade\": 1, \"preco\": 8.00}, {\"nome\": \"Kombucha\", \"quantidade\": 1, \"preco\": 5.00}]', '2024-12-20', '14:30:00', '2024-12-20 15:30:00', NULL, NULL, 0, NULL, 'PEND009', 'Pedido novo');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (10, 'IFOOD010', 'Patrícia Dias', '(11) 99999-0000', 'Rua Estados Unidos, 150', 'Jardins', 'Oeste', '-23.5701', '-46.6734', 48.90, 'pix', '[{\"nome\": \"Risotto\", \"quantidade\": 1, \"preco\": 38.90}, {\"nome\": \"Vinho\", \"quantidade\": 1, \"preco\": 10.00}]', '2024-12-20', '20:00:00', '2024-12-20 21:00:00', NULL, NULL, 0, NULL, 'PEND010', 'Aguardando confirmação');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (11, 'IFOOD011', 'Eduardo Silva', '(11) 88888-1111', 'Av. Ibirapuera, 2000', 'Ibirapuera', 'Sul', '-23.5912', '-46.6634', 22.50, 'dinheiro', '[{\"nome\": \"Tapioca\", \"quantidade\": 1, \"preco\": 15.50}, {\"nome\": \"Café\", \"quantidade\": 1, \"preco\": 7.00}]', '2024-12-19', '08:30:00', '2024-12-19 09:30:00', NULL, NULL, -1, NULL, 'CANC011', 'Cliente cancelou');",
                    "INSERT INTO pedido (id, id_ifood, nome_cliente, telefone_cliente, endereco_entrega, entrega_bairro, region, latitude, longitude, value, tipo_pagamento, items, data_pedido, horario_pedido, previsao_entrega, horario_saida, horario_entrega, status_pedido, motoboy_responsavel, localizador, observacoes) VALUES (12, 'IFOOD012', 'Camila Torres', '(11) 77777-2222', 'Rua Pamplona, 400', 'Jardins', 'Oeste', '-23.5723', '-46.6745', 31.40, 'cartao', '[{\"nome\": \"Salada Caesar\", \"quantidade\": 1, \"preco\": 24.40}, {\"nome\": \"Água com Gás\", \"quantidade\": 1, \"preco\": 7.00}]', '2024-12-19', '15:45:00', '2024-12-19 16:45:00', NULL, NULL, -1, NULL, 'CANC012', 'Problema no pagamento');"
                };
                
                foreach (var insert in pedidoInserts)
                {
                    await ExecuteCommand(connection, insert);
                }
                
                Console.WriteLine("🔄 Atualizando sequences...");
                await ExecuteCommand(connection, "SELECT setval('motoboy_id_seq', (SELECT MAX(id) FROM motoboy));");
                await ExecuteCommand(connection, "SELECT setval('pedido_id_seq', (SELECT MAX(id) FROM pedido));");
                
                Console.WriteLine("\n📊 Verificando dados inseridos:");
                await VerifyData(connection);
                
                Console.WriteLine("\n✅ Dados fictícios inseridos com sucesso!");
                Console.WriteLine("\n🎯 Cenários disponíveis para teste:");
                Console.WriteLine("   • 3 pedidos ENTREGUES (status=3) - com motoboys atribuídos");
                Console.WriteLine("   • 2 pedidos EM ENTREGA (status=2) - com motoboys atribuídos");
                Console.WriteLine("   • 2 pedidos ATRIBUÍDOS (status=1) - com motoboys atribuídos");
                Console.WriteLine("   • 3 pedidos PENDENTES (status=0) - sem motoboy");
                Console.WriteLine("   • 2 pedidos CANCELADOS (status=-1) - sem motoboy");
                Console.WriteLine("   • 5 motoboys cadastrados (4 ativos, 1 inativo)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            }
            
            Console.WriteLine("\nPressione qualquer tecla para sair...");
            Console.ReadKey();
        }
        
        static async Task ExecuteCommand(NpgsqlConnection connection, string sql)
        {
            try
            {
                using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro ao executar comando SQL: {ex.Message}");
                throw;
            }
        }
        
        static async Task VerifyData(NpgsqlConnection connection)
        {
            // Contar motoboys
            using var motoboyCmd = new NpgsqlCommand("SELECT COUNT(*) FROM motoboy", connection);
            var motoboyCount = await motoboyCmd.ExecuteScalarAsync();
            Console.WriteLine($"   Motoboys: {motoboyCount}");
            
            // Contar pedidos por status
            var statusQueries = new Dictionary<string, int>
            {
                { "Entregues (status=3)", 3 },
                { "Em entrega (status=2)", 2 },
                { "Atribuídos (status=1)", 1 },
                { "Pendentes (status=0)", 0 },
                { "Cancelados (status=-1)", -1 }
            };
            
            foreach (var status in statusQueries)
            {
                using var pedidoCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM pedido WHERE status_pedido = {status.Value}", connection);
                var count = await pedidoCmd.ExecuteScalarAsync();
                Console.WriteLine($"   {status.Key}: {count}");
            }
        }
    }
}
