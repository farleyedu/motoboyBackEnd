using Npgsql;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ZippyGo.Scripts
{
    class ExecutarDadosFicticios
    {
        private static readonly string ConnectionString = "Host=hopper.proxy.rlwy.net;Port=51122;Database=railway;Username=postgres;Password=KvBJrxnTRewIOfOkvMIZdJBLUQoROmpR;SSL Mode=Require;Trust Server Certificate=true;";
        
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("🚀 Iniciando inserção de dados fictícios...");
                
                // Ler o arquivo SQL
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "dados_ficticios.sql");
                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"❌ Arquivo não encontrado: {scriptPath}");
                    return;
                }
                
                var sqlScript = await File.ReadAllTextAsync(scriptPath);
                
                // Dividir o script em comandos individuais (separados por ';')
                var commands = sqlScript.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                
                using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                
                Console.WriteLine("✅ Conectado ao PostgreSQL");
                
                int commandCount = 0;
                foreach (var command in commands)
                {
                    var trimmedCommand = command.Trim();
                    
                    // Pular comentários e linhas vazias
                    if (string.IsNullOrWhiteSpace(trimmedCommand) || 
                        trimmedCommand.StartsWith("--") || 
                        trimmedCommand.StartsWith("/*") ||
                        trimmedCommand.ToUpper().StartsWith("PRINT"))
                    {
                        continue;
                    }
                    
                    try
                    {
                        using var cmd = new NpgsqlCommand(trimmedCommand, connection);
                        await cmd.ExecuteNonQueryAsync();
                        commandCount++;
                        
                        if (commandCount % 5 == 0)
                        {
                            Console.WriteLine($"📝 Executados {commandCount} comandos...");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Erro no comando: {trimmedCommand.Substring(0, Math.Min(50, trimmedCommand.Length))}...");
                        Console.WriteLine($"   Erro: {ex.Message}");
                        // Continuar com os próximos comandos
                    }
                }
                
                Console.WriteLine($"✅ Script executado! Total de comandos: {commandCount}");
                
                // Verificar dados inseridos
                Console.WriteLine("\n📊 Verificando dados inseridos:");
                
                using var verifyCmd = new NpgsqlCommand("SELECT COUNT(*) FROM motoboy", connection);
                var motoboyCount = await verifyCmd.ExecuteScalarAsync();
                Console.WriteLine($"   Motoboys: {motoboyCount}");
                
                verifyCmd.CommandText = "SELECT COUNT(*) FROM pedido";
                var pedidoCount = await verifyCmd.ExecuteScalarAsync();
                Console.WriteLine($"   Pedidos: {pedidoCount}");
                
                verifyCmd.CommandText = "SELECT status_pedido, COUNT(*) FROM pedido GROUP BY status_pedido ORDER BY status_pedido";
                using var reader = await verifyCmd.ExecuteReaderAsync();
                Console.WriteLine("   Pedidos por status:");
                while (await reader.ReadAsync())
                {
                    var status = reader.GetInt32(0);
                    var count = reader.GetInt64(1);
                    var statusName = status switch
                    {
                        -1 => "Cancelado",
                        0 => "Pendente", 
                        1 => "Atribuído",
                        2 => "Em Entrega",
                        3 => "Entregue",
                        _ => "Desconhecido"
                    };
                    Console.WriteLine($"     {statusName} ({status}): {count}");
                }
                
                Console.WriteLine("\n🎯 Dados fictícios inseridos com sucesso!");
                Console.WriteLine("🔗 Agora você pode testar os endpoints da API.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro geral: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            }
            
            Console.WriteLine("\nPressione qualquer tecla para sair...");
            Console.ReadKey();
        }
    }
}