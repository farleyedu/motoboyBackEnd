using Npgsql;
using System.Text;

namespace motoboyBackEnd.Utils;

public class DatabaseMigrator
{
    private readonly string _connectionString;

    public DatabaseMigrator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<bool> ExecuteSqlFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"❌ Arquivo não encontrado: {filePath}");
                return false;
            }

            var sqlContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            
            // Dividir o script em comandos individuais
            var commands = sqlContent.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(cmd => !string.IsNullOrWhiteSpace(cmd.Trim()))
                .Select(cmd => cmd.Trim())
                .Where(cmd => !cmd.StartsWith("--") && !string.IsNullOrEmpty(cmd))
                .ToList();

            Console.WriteLine($"📄 Executando {commands.Count} comandos SQL de {Path.GetFileName(filePath)}");

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var successCount = 0;
            var errorCount = 0;

            foreach (var command in commands)
            {
                try
                {
                    using var cmd = new NpgsqlCommand(command, connection);
                    var result = await cmd.ExecuteNonQueryAsync();
                    successCount++;
                    
                    // Log apenas comandos importantes
                    if (command.ToUpper().Contains("CREATE TABLE") || 
                        command.ToUpper().Contains("INSERT INTO") ||
                        command.ToUpper().Contains("SELECT 'Exemplo"))
                    {
                        Console.WriteLine($"✅ {command.Substring(0, Math.Min(50, command.Length))}...");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($"❌ Erro ao executar comando: {ex.Message}");
                    Console.WriteLine($"   Comando: {command.Substring(0, Math.Min(100, command.Length))}...");
                }
            }

            Console.WriteLine($"\n📊 Resultado: {successCount} sucessos, {errorCount} erros");
            return errorCount == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro geral ao executar script: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ExecuteSqlAsync(string sql)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
            
            Console.WriteLine($"✅ SQL executado com sucesso");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao executar SQL: {ex.Message}");
            return false;
        }
    }

    public async Task<List<Dictionary<string, object>>> QueryAsync(string sql)
    {
        var results = new List<Dictionary<string, object>>();
        
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            using var cmd = new NpgsqlCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i);
                }
                results.Add(row);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao executar query: {ex.Message}");
        }
        
        return results;
    }
}