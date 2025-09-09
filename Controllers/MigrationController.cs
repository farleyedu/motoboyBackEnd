using Microsoft.AspNetCore.Mvc;
using motoboyBackEnd.Utils;

namespace motoboyBackEnd.Controllers;

[Route("api/[controller]")]
[ApiController]
public class MigrationController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public MigrationController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Executa script SQL de migração - APENAS PARA DESENVOLVIMENTO
    /// </summary>
    [HttpPost("execute-script/{scriptName}")]
    public async Task<IActionResult> ExecuteScript(string scriptName)
    {
        try
        {
            // Validar nome do script por segurança
            if (!scriptName.EndsWith(".sql") || scriptName.Contains("..") || scriptName.Contains("/") || scriptName.Contains("\\"))
            {
                return BadRequest(new { success = false, error = "Nome de script inválido" });
            }

            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { success = false, error = "Connection string não encontrada" });
            }

            var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", scriptName);
            
            if (!System.IO.File.Exists(scriptPath))
            {
                return NotFound(new { success = false, error = $"Script {scriptName} não encontrado" });
            }

            var migrator = new DatabaseMigrator(connectionString);
            var success = await migrator.ExecuteSqlFileAsync(scriptPath);

            if (success)
            {
                return Ok(new { 
                    success = true, 
                    message = $"Script {scriptName} executado com sucesso",
                    scriptPath = scriptPath
                });
            }
            else
            {
                return StatusCode(500, new { 
                    success = false, 
                    error = $"Erro ao executar script {scriptName}"
                });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { 
                success = false, 
                error = ex.Message,
                stackTrace = ex.StackTrace
            });
        }
    }

    /// <summary>
    /// Lista scripts disponíveis
    /// </summary>
    [HttpGet("scripts")]
    public IActionResult ListScripts()
    {
        try
        {
            var scriptsPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts");
            
            if (!Directory.Exists(scriptsPath))
            {
                return Ok(new { success = true, scripts = new string[0] });
            }

            var scripts = Directory.GetFiles(scriptsPath, "*.sql")
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();

            return Ok(new { 
                success = true, 
                scripts = scripts,
                scriptsPath = scriptsPath
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { 
                success = false, 
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Verifica status das tabelas
    /// </summary>
    [HttpGet("table-status")]
    public async Task<IActionResult> GetTableStatus()
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            var migrator = new DatabaseMigrator(connectionString);

            var sql = @"
                SELECT 
                    table_name,
                    (SELECT COUNT(*) FROM information_schema.columns WHERE table_name = t.table_name) as column_count
                FROM information_schema.tables t
                WHERE table_schema = 'public' 
                AND table_type = 'BASE TABLE'
                ORDER BY table_name;
            ";

            var tables = await migrator.QueryAsync(sql);

            // Verificar contagem de registros para tabelas principais
            var counts = new Dictionary<string, object>();
            var mainTables = new[] { "pedido", "motoboy", "itens_pedido", "timeline_pedido" };
            
            foreach (var tableName in mainTables)
            {
                try
                {
                    var countSql = $"SELECT COUNT(*) as total FROM {tableName}";
                    var result = await migrator.QueryAsync(countSql);
                    counts[tableName] = result.FirstOrDefault()?["total"] ?? 0;
                }
                catch
                {
                    counts[tableName] = "Tabela não existe";
                }
            }

            return Ok(new { 
                success = true, 
                tables = tables,
                recordCounts = counts
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { 
                success = false, 
                error = ex.Message
            });
        }
    }
}