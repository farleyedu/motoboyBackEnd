# Script PowerShell para executar SQL no PostgreSQL
param(
    [string]$SqlFile,
    [string]$ConnectionString = "Host=localhost;Database=zippy_db;Username=postgres;Password=123456"
)

if (-not $SqlFile) {
    Write-Host "Uso: .\executar_sql.ps1 -SqlFile 'caminho_do_arquivo.sql'"
    exit 1
}

if (-not (Test-Path $SqlFile)) {
    Write-Host "Arquivo SQL não encontrado: $SqlFile"
    exit 1
}

try {
    # Carregar o conteúdo do arquivo SQL
    $sqlContent = Get-Content -Path $SqlFile -Raw
    
    Write-Host "Executando SQL do arquivo: $SqlFile"
    Write-Host "Conexão: $ConnectionString"
    
    # Usar dotnet para executar o SQL
    $tempCsFile = "temp_sql_executor.cs"
    
    $csContent = @"
using System;
using System.IO;
using Npgsql;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var connectionString = args[0];
        var sqlContent = args[1];
        
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            
            using var command = new NpgsqlCommand(sqlContent, connection);
            var result = await command.ExecuteNonQueryAsync();
            
            Console.WriteLine($"SQL executado com sucesso. Linhas afetadas: {result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao executar SQL: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
"@
    
    # Criar arquivo temporário
    Set-Content -Path $tempCsFile -Value $csContent
    
    # Executar com dotnet
    $result = dotnet run --project . -- "$ConnectionString" "$sqlContent"
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Script SQL executado com sucesso!"
    } else {
        Write-Host "Erro na execução do script SQL"
        exit 1
    }
    
    # Limpar arquivo temporário
    if (Test-Path $tempCsFile) {
        Remove-Item $tempCsFile
    }
    
} catch {
    Write-Host "Erro: $($_.Exception.Message)"
    exit 1
}