using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Automation.Repository
{
    public class EstabelecimentoSelectionRepository : IEstabelecimentoSelectionRepository
    {
        private readonly string _connectionString;

        public EstabelecimentoSelectionRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? configuration["ConnectionStrings:DefaultConnection"]
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não configurada.");
        }

        public async Task<UsuarioTenant?> ObterUsuarioAsync(int userId)
        {
            const string sql = @"
SELECT id,
       nome,
       email,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       deleted_at
  FROM usuario
 WHERE id = @UserId";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<UsuarioTenant>(sql, new { UserId = userId });
        }

        public async Task<IReadOnlyCollection<UsuarioEstabelecimentoVinculo>> ListarEstabelecimentosUsuarioAsync(int userId)
        {
            const string sql = @"
SELECT  ue.id                  AS VinculoId,
        ue.id_estabelecimento  AS EstabelecimentoId,
        e.nome_fantasia        AS Nome,
        te.nome                AS TipoEstabelecimento,
        ue.status              AS StatusVinculo,
        ue.ativo               AS VinculoAtivo,
        ue.tipo_acesso         AS TipoAcesso,
        e.status               AS StatusEstabelecimento,
        e.ativo                AS EstabelecimentoAtivo,
        e.plano                AS Plano
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE ue.id_usuario = @UserId
 ORDER BY e.nome_fantasia";

            await using var connection = new NpgsqlConnection(_connectionString);
            var result = await connection.QueryAsync<UsuarioEstabelecimentoVinculo>(sql, new { UserId = userId });
            return result.ToArray();
        }

        public async Task<IReadOnlyCollection<EstabelecimentoAtivoResumo>> ListarEstabelecimentosAtivosAsync()
        {
            const string sql = @"
SELECT  e.id              AS EstabelecimentoId,
        e.nome_fantasia   AS Nome,
        te.nome           AS TipoEstabelecimento,
        e.status          AS StatusEstabelecimento,
        e.ativo           AS EstabelecimentoAtivo,
        e.plano           AS Plano
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE (e.status = 'ativo' OR e.status IS NULL)
   AND (e.ativo IS NULL OR e.ativo = TRUE)
 ORDER BY e.nome_fantasia";

            await using var connection = new NpgsqlConnection(_connectionString);
            var result = await connection.QueryAsync<EstabelecimentoAtivoResumo>(sql);
            return result.ToArray();
        }

        public async Task<EstabelecimentoDetalhe?> ObterEstabelecimentoDetalheAsync(Guid estabelecimentoId)
        {
            const string sql = @"
SELECT  e.id            AS Id,
        e.nome_fantasia AS Nome,
        te.nome         AS TipoEstabelecimento,
        e.plano         AS Plano,
        e.status        AS Status,
        e.ativo         AS Ativo
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE e.id = @EstabelecimentoId";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<EstabelecimentoDetalhe>(sql, new { EstabelecimentoId = estabelecimentoId });
        }

        public async Task<UsuarioEstabelecimentoAcesso?> ObterVinculoAsync(int userId, Guid estabelecimentoId)
        {
            const string sql = @"
SELECT  ue.id                   AS Id,
        ue.id_usuario           AS UsuarioId,
        ue.id_estabelecimento   AS EstabelecimentoId,
        ue.status               AS Status,
        ue.ativo                AS VinculoAtivo,
        ue.tipo_acesso          AS TipoAcesso,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas
  FROM usuario_estabelecimentos ue
 WHERE ue.id_usuario = @UserId
   AND ue.id_estabelecimento = @EstabelecimentoId
 ORDER BY ue.data_criacao DESC
 LIMIT 1";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<UsuarioEstabelecimentoAcesso>(sql, new { UserId = userId, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<UsuarioEstabelecimentoAcesso?> ObterVinculoPorIdAsync(Guid vinculoId)
        {
            const string sql = @"
SELECT  ue.id                   AS Id,
        ue.id_usuario           AS UsuarioId,
        ue.id_estabelecimento   AS EstabelecimentoId,
        ue.status               AS Status,
        ue.ativo                AS VinculoAtivo,
        ue.tipo_acesso          AS TipoAcesso,
        ue.permissoes_customizadas::text AS PermissoesCustomizadas
  FROM usuario_estabelecimentos ue
 WHERE ue.id = @VinculoId
 LIMIT 1";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.QueryFirstOrDefaultAsync<UsuarioEstabelecimentoAcesso>(sql, new { VinculoId = vinculoId });
        }

        public async Task AtualizarPermissoesCustomizadasAsync(Guid vinculoId, string? permissoesCustomizadasJson)
        {
            const string sql = @"
UPDATE usuario_estabelecimentos
   SET permissoes_customizadas = CASE
                                     WHEN @PermissoesCustomizadas IS NULL THEN NULL
                                     ELSE @PermissoesCustomizadas::jsonb
                                 END
 WHERE id = @VinculoId";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new
            {
                VinculoId = vinculoId,
                PermissoesCustomizadas = permissoesCustomizadasJson
            });
        }

        public async Task AtualizarUltimoEstabelecimentoAsync(int userId, Guid estabelecimentoId)
        {
            const string sql = @"
UPDATE usuario
   SET ultimo_estabelecimento_acessado = @EstabelecimentoId,
       updated_at = NOW()
 WHERE id = @UserId";

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.ExecuteAsync(sql, new { UserId = userId, EstabelecimentoId = estabelecimentoId });
        }

        public async Task<Dictionary<string, List<string>>> ObterPermissoesPorTipoAsync(string? tipoAcesso)
        {
            if (string.IsNullOrWhiteSpace(tipoAcesso))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            const string sql = @"
SELECT  m.nome              AS Modulo,
        pp.permissoes::text AS Permissoes
  FROM permissoes_padrao pp
  JOIN modulos_disponiveis m ON m.id = pp.id_modulo
 WHERE pp.tipo_acesso = @TipoAcesso";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<PermissaoRow>(sql, new { TipoAcesso = tipoAcesso });

            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Modulo))
                {
                    continue;
                }

                dict[row.Modulo] = ParsePermissoes(row.Permissoes);
            }

            return dict;
        }

        private static List<string> ParsePermissoes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            try
            {
                if (raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(raw);
                    if (parsed != null)
                    {
                        return parsed
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => p.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }
            catch
            {
                // Ignora erros de parse tentando fallback para split simples
            }

            var separadores = new[] { ',', ';', '|' };
            return raw.Split(separadores, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private sealed class PermissaoRow
        {
            public string Modulo { get; set; } = string.Empty;
            public string? Permissoes { get; set; }
        }
    }
}
