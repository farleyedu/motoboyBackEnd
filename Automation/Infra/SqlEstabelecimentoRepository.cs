// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
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

        public async Task<IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto>> ListarEstabelecimentosComWhatsappAtivoAsync(Guid? excluirEstabelecimentoId = null)
        {
            const string sql = @"
SELECT DISTINCT
       e.id            AS Id,
       e.nome_fantasia AS Nome,
       te.nome         AS TipoEstabelecimento
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE (e.status = 'ativo' OR e.status IS NULL)
   AND (e.ativo IS NULL OR e.ativo = TRUE)
   AND (@ExcluirEstabelecimentoId IS NULL OR e.id <> @ExcluirEstabelecimentoId)
   AND EXISTS (
       SELECT 1
         FROM unnest(e.modulos_ativos) AS modulo
        WHERE lower(trim(modulo::text)) = 'whatsapp'
   )
 ORDER BY e.nome_fantasia;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                var estabelecimentos = await connection.QueryAsync<EstabelecimentoWhatsappAtivoDto>(
                    sql,
                    new { ExcluirEstabelecimentoId = excluirEstabelecimentoId });

                return estabelecimentos.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar estabelecimentos com whatsapp ativo");
                return Array.Empty<EstabelecimentoWhatsappAtivoDto>();
            }
        }

        public async Task<string?> ObterNomeFantasiaAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT nome_fantasia
  FROM estabelecimentos
 WHERE id = @IdEstabelecimento
 LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.ExecuteScalarAsync<string?>(sql, new { IdEstabelecimento = idEstabelecimento });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar nome do estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return null;
            }
        }

        public async Task<EstabelecimentoPublicoResumo?> ObterResumoPublicoAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT id,
       nome_fantasia AS NomeFantasia,
       slug
  FROM estabelecimentos
 WHERE id = @IdEstabelecimento
   AND COALESCE(ativo, TRUE) = TRUE
   AND COALESCE(status, 'ativo') = 'ativo'
 LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.QueryFirstOrDefaultAsync<EstabelecimentoPublicoResumo>(sql, new
                {
                    IdEstabelecimento = idEstabelecimento
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar resumo publico do estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return null;
            }
        }

        public async Task<string?> ObterTipoEstabelecimentoAsync(Guid idEstabelecimento)
        {
            const string sql = @"
SELECT te.slug
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE e.id = @IdEstabelecimento
 LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.ExecuteScalarAsync<string?>(sql, new { IdEstabelecimento = idEstabelecimento });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar tipo do estabelecimento {IdEstabelecimento}", idEstabelecimento);
                return null;
            }
        }

        public async Task<EstabelecimentoPublicoResumo?> ObterResumoPublicoPorSlugAsync(string slug)
        {
            const string sql = @"
SELECT id,
       nome_fantasia AS NomeFantasia,
       slug
  FROM estabelecimentos
 WHERE lower(slug) = lower(@Slug)
   AND COALESCE(ativo, TRUE) = TRUE
   AND COALESCE(status, 'ativo') = 'ativo'
 LIMIT 1;";

            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                return await connection.QueryFirstOrDefaultAsync<EstabelecimentoPublicoResumo>(sql, new
                {
                    Slug = slug.Trim()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar resumo publico do estabelecimento pelo slug {Slug}", slug);
                return null;
            }
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
