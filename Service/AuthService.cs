using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.DTOs.Auth;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Service
{
    public class AuthService : IAuthService
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;
        private readonly IJwtService _jwtService;

        public AuthService(IConfiguration configuration, IJwtService jwtService)
        {
            _configuration = configuration;
            _jwtService = jwtService;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? configuration["ConnectionStrings:DefaultConnection"]
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' não encontrada.");
        }

        public async Task<TokenResponse> LoginAsync(LoginRequest request)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            const string sqlUsuario = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       deleted_at
  FROM usuario
 WHERE LOWER(email) = LOWER(@Email)
   AND deleted_at IS NULL";

            var usuario = await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sqlUsuario, new { request.Email });

            if (usuario == null)
            {
                throw new UnauthorizedAccessException("Usuário ou senha inválidos.");
            }

            // ⚠️ TEMPORÁRIO: Comparação direta de senhas (SEM BCrypt)
            // TODO: Trocar por BCrypt antes de ir para produção!
            if (string.IsNullOrWhiteSpace(usuario.Senha) ||
                !string.Equals(request.Senha, usuario.Senha, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Usuário ou senha inválidos.");
            }

            // Super admin sem estabelecimento: token básico sem contexto de estabelecimento
            if (usuario.IsSuperAdmin && usuario.UltimoEstabelecimentoAcessado == null)
            {
                return await BuildTokenResponseAsync(connection, usuario, null);
            }

            var estabelecimentoId = usuario.UltimoEstabelecimentoAcessado
                                 ?? await ObterPrimeiroEstabelecimentoDoUsuarioAsync(connection, usuario.Id);

            return await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
        }

        public async Task<TokenResponse> SelecionarEstabelecimentoAsync(Guid userId, Guid estabelecimentoId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            var usuario = await ObterUsuarioPorIdAsync(connection, userId);

            if (usuario == null || usuario.DeletedAt != null)
            {
                throw new UnauthorizedAccessException("Usuário não encontrado ou inativo.");
            }

            // Verifica acesso ao estabelecimento (super admin sempre pode)
            var contexto = await ObterContextoEstabelecimentoAsync(connection, usuario.Id, estabelecimentoId, usuario.IsSuperAdmin);

            if (!usuario.IsSuperAdmin && contexto.VinculoId == null)
            {
                throw new UnauthorizedAccessException("Usuário não possui acesso a este estabelecimento.");
            }

            const string sqlUpdateUltimo = @"
UPDATE usuario
   SET ultimo_estabelecimento_acessado = @EstabelecimentoId
 WHERE id = @UserId";

            await connection.ExecuteAsync(sqlUpdateUltimo, new { EstabelecimentoId = estabelecimentoId, UserId = userId });

            // Atualiza cache local
            usuario.UltimoEstabelecimentoAcessado = estabelecimentoId;

            return await BuildTokenResponseAsync(connection, usuario, estabelecimentoId);
        }

        public async Task<List<EstabelecimentoDisponivelDTO>> ListarEstabelecimentosDisponiveisAsync(Guid userId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);

            var usuario = await ObterUsuarioPorIdAsync(connection, userId);

            if (usuario == null || usuario.DeletedAt != null)
            {
                throw new UnauthorizedAccessException("Usuário não encontrado ou inativo.");
            }

            IEnumerable<EstabelecimentoDisponivelRow> estabelecimentos;

            if (usuario.IsSuperAdmin)
            {
                const string sqlTodos = @"
SELECT  e.id              AS Id,
        e.nome_fantasia   AS Nome,
        te.nome           AS Tipo,
        ''::text          AS TipoAcesso,
        'ativo'::text     AS Status
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE (e.ativo IS NULL OR e.ativo = TRUE)
 ORDER BY e.nome_fantasia";

                estabelecimentos = await connection.QueryAsync<EstabelecimentoDisponivelRow>(sqlTodos);
            }
            else
            {
                const string sqlVinculados = @"
SELECT  e.id              AS Id,
        e.nome_fantasia   AS Nome,
        te.nome           AS Tipo,
        ue.tipo_acesso    AS TipoAcesso,
        ue.status         AS Status
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE ue.id_usuario = @UserId
   AND ue.ativo = TRUE
   AND ue.status = 'ativo'
 ORDER BY e.nome_fantasia";

                estabelecimentos = await connection.QueryAsync<EstabelecimentoDisponivelRow>(sqlVinculados, new { UserId = userId });
            }

            var atualId = usuario.UltimoEstabelecimentoAcessado;

            return estabelecimentos
                .Select(e => new EstabelecimentoDisponivelDTO
                {
                    Id = e.Id,
                    Nome = e.Nome ?? string.Empty,
                    Tipo = e.Tipo ?? string.Empty,
                    TipoAcesso = e.TipoAcesso ?? string.Empty,
                    Status = e.Status ?? string.Empty,
                    IsAtual = atualId.HasValue && atualId.Value == e.Id
                })
                .ToList();
        }

        private async Task<UsuarioDb?> ObterUsuarioPorIdAsync(NpgsqlConnection connection, Guid userId)
        {
            const string sql = @"
SELECT id,
       nome,
       email,
       senha,
       is_super_admin,
       ultimo_estabelecimento_acessado,
       deleted_at
  FROM usuario
 WHERE id = @UserId";

            return await connection.QueryFirstOrDefaultAsync<UsuarioDb>(sql, new { UserId = userId });
        }

        private async Task<Guid> ObterPrimeiroEstabelecimentoDoUsuarioAsync(NpgsqlConnection connection, int userId)
        {
            const string sql = @"
SELECT id_estabelecimento
  FROM usuario_estabelecimentos
 WHERE id_usuario = @UserId
   AND ativo = TRUE
   AND status = 'ativo'
 ORDER BY data_criacao ASC
 LIMIT 1";

            var estabelecimentoId = await connection.ExecuteScalarAsync<Guid?>(sql, new { UserId = userId });

            if (!estabelecimentoId.HasValue)
            {
                throw new UnauthorizedAccessException("Usuário não possui vínculo ativo com estabelecimentos.");
            }

            return estabelecimentoId.Value;
        }

        private async Task<TokenResponse> BuildTokenResponseAsync(
            NpgsqlConnection connection,
            UsuarioDb usuario,
            Guid? estabelecimentoId)
        {
            JwtPayload payload;
            EstabelecimentoInfo? estabelecimentoInfo = null;

            if (estabelecimentoId.HasValue)
            {
                var contexto = await ObterContextoEstabelecimentoAsync(connection, usuario.Id, estabelecimentoId.Value, usuario.IsSuperAdmin);

                if (!usuario.IsSuperAdmin && contexto.VinculoId == null)
                {
                    throw new UnauthorizedAccessException("Usuário não possui acesso ao estabelecimento selecionado.");
                }

                var permissoes = !string.IsNullOrWhiteSpace(contexto.TipoAcesso)
                    ? await ObterPermissoesAsync(connection, contexto.TipoAcesso)
                    : new Dictionary<string, List<string>>();

                payload = new JwtPayload
                {
                    UserId = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    EstabelecimentoId = contexto.EstabelecimentoId,
                    EstabelecimentoNome = contexto.EstabelecimentoNome,
                    TipoEstabelecimento = contexto.TipoEstabelecimento,
                    TipoAcesso = contexto.TipoAcesso,
                    VinculoId = contexto.VinculoId,
                    Permissoes = permissoes
                };

                estabelecimentoInfo = new EstabelecimentoInfo
                {
                    Id = contexto.EstabelecimentoId,
                    Nome = contexto.EstabelecimentoNome ?? string.Empty,
                    Tipo = contexto.TipoEstabelecimento ?? string.Empty,
                    TipoAcesso = contexto.TipoAcesso ?? string.Empty
                };
            }
            else
            {
                payload = new JwtPayload
                {
                    UserId = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    Permissoes = new Dictionary<string, List<string>>()
                };
            }

            var jwtSection = _configuration.GetSection("Jwt");
            var expirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var minutes)
                ? minutes
                : 60;

            var accessToken = _jwtService.GenerateToken(payload);
            var refreshToken = _jwtService.GenerateRefreshToken();

            return new TokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = expirationMinutes * 60,
                User = new UserInfo
                {
                    Id = usuario.Id,
                    Nome = usuario.Nome ?? string.Empty,
                    Email = usuario.Email ?? string.Empty,
                    IsSuperAdmin = usuario.IsSuperAdmin,
                    EstabelecimentoAtual = estabelecimentoInfo
                }
            };
        }

        private async Task<EstabelecimentoContextoDb> ObterContextoEstabelecimentoAsync(
            NpgsqlConnection connection,
            int userId,
            Guid estabelecimentoId,
            bool isSuperAdmin)
        {
            const string sql = @"
SELECT  e.id                    AS EstabelecimentoId,
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        ue.id                   AS VinculoId,
        ue.tipo_acesso          AS TipoAcesso
  FROM estabelecimentos e
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue 
         ON ue.id_estabelecimento = e.id 
        AND ue.id_usuario = @UserId 
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
 WHERE e.id = @EstabelecimentoId";

            var contexto = await connection.QueryFirstOrDefaultAsync<EstabelecimentoContextoDb>(sql, new
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            });

            if (contexto == null)
            {
                throw new UnauthorizedAccessException("Estabelecimento não encontrado.");
            }

            if (!isSuperAdmin && contexto.VinculoId == null)
            {
                throw new UnauthorizedAccessException("Usuário não possui vínculo ativo com este estabelecimento.");
            }

            return contexto;
        }

        private async Task<Dictionary<string, List<string>>> ObterPermissoesAsync(
            NpgsqlConnection connection,
            string? tipoAcesso)
        {
            // Apenas permissões padrão - sem customizadas
            if (string.IsNullOrWhiteSpace(tipoAcesso))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            const string sqlPadrao = @"
SELECT  m.nome                  AS Modulo,
        pp.permissoes::text     AS Permissoes
  FROM permissoes_padrao pp
  JOIN modulos_disponiveis m ON m.id = pp.id_modulo
 WHERE pp.tipo_acesso = @TipoAcesso";

            var linhas = await connection.QueryAsync<PermissaoRow>(sqlPadrao, new { TipoAcesso = tipoAcesso });

            var permissoes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var linha in linhas)
            {
                if (!string.IsNullOrWhiteSpace(linha.Modulo))
                {
                    permissoes[linha.Modulo] = ParsePermissoes(linha.Permissoes);
                }
            }

            return permissoes;
        }

        private static List<string> ParsePermissoes(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            raw = raw.Trim();

            try
            {
                if (raw.StartsWith("[", StringComparison.Ordinal))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(raw);
                    if (list != null)
                    {
                        return list
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => p.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
            }
            catch
            {
                // Ignora erros de parse de JSON e tenta fallback
            }

            var separadores = new[] { ',', ';', '|' };

            return raw
                .Split(separadores, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private sealed class UsuarioDb
        {
            public int Id { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Senha { get; set; } = string.Empty;
            public bool IsSuperAdmin { get; set; }
            public Guid? UltimoEstabelecimentoAcessado { get; set; }
            public DateTime? DeletedAt { get; set; }
        }

        private sealed class EstabelecimentoContextoDb
        {
            public Guid EstabelecimentoId { get; set; }
            public string? EstabelecimentoNome { get; set; }
            public string? TipoEstabelecimento { get; set; }
            public Guid? VinculoId { get; set; }
            public string? TipoAcesso { get; set; }
        }

        private sealed class PermissaoRow
        {
            public string Modulo { get; set; } = string.Empty;
            public string? Permissoes { get; set; }
        }

        private sealed class EstabelecimentoDisponivelRow
        {
            public Guid Id { get; set; }
            public string? Nome { get; set; }
            public string? Tipo { get; set; }
            public string? TipoAcesso { get; set; }
            public string? Status { get; set; }
        }
    }
}