// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Automation.Infra
{
    public class SqlEstabelecimentoRepository : IEstabelecimentoRepository
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlEstabelecimentoRepository> _logger;

        public SqlEstabelecimentoRepository(Microsoft.Extensions.Configuration.IConfiguration config, ILogger<SqlEstabelecimentoRepository> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")
                                 ?? config["ConnectionStrings:DefaultConnection"]
                                 ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao encontrada.");
            _logger = logger;
        }

        public async Task<IReadOnlyCollection<string>> ObterModulosAtivosAsync(Guid idEstabelecimento)
        {
            try
            {
                const string sql = @"SELECT unnest(modulos_ativos)::text as modulo
                                    FROM estabelecimentos 
                                    WHERE id = @IdEstabelecimento 
                                      AND ativo = TRUE;";

                await using var connection = new NpgsqlConnection(_connectionString);
                var modulos = await connection.QueryAsync<string>(sql, new { IdEstabelecimento = idEstabelecimento });

                var modulosArray = modulos?.ToArray() ?? Array.Empty<string>();

                if (modulosArray.Length == 0)
                {
                    _logger.LogWarning("Nenhum módulo ativo encontrado para estabelecimento {IdEstabelecimento}", idEstabelecimento);
                    return Array.Empty<string>();
                }

                return modulosArray
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar módulos ativos para estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return Array.Empty<string>();
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================