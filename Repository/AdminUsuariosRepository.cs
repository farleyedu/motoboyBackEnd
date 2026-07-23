using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model.AdminUsers;
using APIBack.Repository.Interface;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Repository
{
    public class AdminUsuariosRepository : IAdminUsuariosRepository
    {
        private readonly string _connectionString;

        public AdminUsuariosRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' nao configurada.");
        }

        public async Task<AdminActorSnapshot?> ObterActorSnapshotAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin)
        {
            const string sql = @"
SELECT  u.id                    AS UserId,
        u.is_super_admin        AS IsSuperAdmin,
        e.id_empresa            AS EmpresaId,
        emp.nome_fantasia       AS EmpresaNome,
        COALESCE(uemp.tipo_acesso, 'colaborador') AS CompanyRole,
        e.id                    AS EstabelecimentoId,
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        ue.tipo_acesso          AS EstablishmentRole
  FROM usuario u
  LEFT JOIN estabelecimentos e ON e.id = @EstabelecimentoId
  LEFT JOIN empresas emp ON emp.id = e.id_empresa
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue
         ON ue.id_usuario = u.id
        AND ue.id_estabelecimento = e.id
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
  LEFT JOIN usuario_empresas uemp
         ON uemp.id_usuario = u.id
        AND uemp.id_empresa = e.id_empresa
        AND uemp.ativo = TRUE
        AND uemp.status = 'ativo'
 WHERE u.id = @UserId
   AND u.deleted_at IS NULL";

            await using var connection = new NpgsqlConnection(_connectionString);
            var snapshot = await connection.QueryFirstOrDefaultAsync<AdminActorSnapshot>(sql, new
            {
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            });

            if (snapshot != null && isSuperAdmin)
            {
                snapshot.IsSuperAdmin = true;
            }

            return snapshot;
        }

        public async Task<IReadOnlyCollection<AdminEstabelecimentoOption>> ListarEstabelecimentosGerenciaveisAsync(int userId, bool isSuperAdmin)
        {
            const string sqlSuperAdmin = @"
SELECT  e.id              AS Id,
        e.id_empresa      AS EmpresaId,
        emp.nome_fantasia AS EmpresaNome,
        e.nome_fantasia   AS Nome,
        te.nome           AS TipoEstabelecimento,
        FALSE             AS IsCurrent
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE (e.ativo IS NULL OR e.ativo = TRUE)
   AND (e.status IS NULL OR e.status = 'ativo')
 ORDER BY emp.nome_fantasia, e.nome_fantasia";

            const string sqlGerente = @"
SELECT DISTINCT
        e.id              AS Id,
        e.id_empresa      AS EmpresaId,
        emp.nome_fantasia AS EmpresaNome,
        e.nome_fantasia   AS Nome,
        te.nome           AS TipoEstabelecimento,
        FALSE             AS IsCurrent
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_estabelecimentos ue
         ON ue.id_estabelecimento = e.id
        AND ue.id_usuario = @UserId
        AND ue.ativo = TRUE
        AND ue.status = 'ativo'
        AND ue.tipo_acesso = 'gerente_estabelecimento'
  LEFT JOIN usuario_empresas uemp
         ON uemp.id_usuario = @UserId
        AND uemp.id_empresa = e.id_empresa
        AND uemp.ativo = TRUE
        AND uemp.status = 'ativo'
        AND uemp.tipo_acesso = 'dono'
 WHERE ((e.ativo IS NULL OR e.ativo = TRUE)
   AND (e.status IS NULL OR e.status = 'ativo'))
   AND (ue.id IS NOT NULL OR uemp.id IS NOT NULL)
 ORDER BY emp.nome_fantasia, e.nome_fantasia";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<AdminEstabelecimentoOption>(
                isSuperAdmin ? sqlSuperAdmin : sqlGerente,
                new { UserId = userId });
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<AdminEmpresaOption>> ListarEmpresasAsync()
        {
            const string sql = @"
SELECT id AS Id,
       nome_fantasia AS Nome
  FROM empresas
 ORDER BY nome_fantasia";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<AdminEmpresaOption>(sql);
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<AdminUsuarioListRow>> ListarUsuariosAsync(Guid empresaId, Guid estabelecimentoId)
        {
            const string sql = @"
SELECT  u.id                    AS Id,
        COALESCE(u.nome, '')    AS Nome,
        COALESCE(u.email, '')   AS Email,
        CASE WHEN u.deleted_at IS NULL THEN 'ativo' ELSE 'inativo' END AS Status,
        e.id_empresa            AS EmpresaId,
        emp.nome_fantasia       AS EmpresaNome,
        NULLIF(uemp.tipo_acesso, 'colaborador') AS CompanyRole,
        e.id                    AS EstabelecimentoId,
        e.nome_fantasia         AS EstabelecimentoNome,
        te.nome                 AS TipoEstabelecimento,
        ue.tipo_acesso          AS EstablishmentRole
  FROM usuario_estabelecimentos ue
  JOIN usuario u ON u.id = ue.id_usuario
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_empresas uemp
         ON uemp.id_usuario = u.id
        AND uemp.id_empresa = e.id_empresa
        AND uemp.ativo = TRUE
        AND uemp.status = 'ativo'
 WHERE e.id_empresa = @EmpresaId
   AND e.id = @EstabelecimentoId
   AND ue.ativo = TRUE
   AND ue.status = 'ativo'
   AND u.deleted_at IS NULL
 ORDER BY u.nome, u.email";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<AdminUsuarioListRow>(sql, new
            {
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId
            });
            return rows.ToArray();
        }

        public async Task<IReadOnlyCollection<AdminTargetEstabelecimento>> ListarEstabelecimentosPorIdsAsync(IReadOnlyCollection<Guid> estabelecimentoIds)
        {
            if (estabelecimentoIds == null || estabelecimentoIds.Count == 0)
            {
                return Array.Empty<AdminTargetEstabelecimento>();
            }

            const string sql = @"
SELECT  e.id              AS Id,
        e.id_empresa      AS EmpresaId,
        emp.nome_fantasia AS EmpresaNome,
        e.nome_fantasia   AS Nome,
        te.nome           AS TipoEstabelecimento,
        COALESCE(e.ativo, TRUE) AS Ativo,
        COALESCE(e.status, 'ativo') AS Status
  FROM estabelecimentos e
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE e.id = ANY(@Ids)";

            await using var connection = new NpgsqlConnection(_connectionString);
            var rows = await connection.QueryAsync<AdminTargetEstabelecimento>(sql, new { Ids = estabelecimentoIds.ToArray() });
            return rows.ToArray();
        }

        public async Task<bool> EmailExisteAsync(string email)
        {
            const string sql = @"
SELECT EXISTS(
    SELECT 1
      FROM usuario
     WHERE LOWER(email) = LOWER(@Email)
       AND deleted_at IS NULL
)";

            await using var connection = new NpgsqlConnection(_connectionString);
            return await connection.ExecuteScalarAsync<bool>(sql, new { Email = email });
        }

        public async Task<AdminCreatedUserSummary> CriarUsuarioAsync(AdminCreateUserCommand command)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            const string sqlUsuario = @"
INSERT INTO usuario (nome, email, senha, is_super_admin, ultimo_estabelecimento_acessado, created_at, updated_at)
VALUES (@Nome, @Email, @Senha, FALSE, @UltimoEstabelecimentoAcessado, NOW(), NOW())
RETURNING id";

            var primeiroEstabelecimento = command.EstabelecimentoIds.FirstOrDefault();
            var userId = await connection.ExecuteScalarAsync<int>(sqlUsuario, new
            {
                Nome = command.Nome,
                Email = command.Email,
                Senha = command.SenhaHash,
                UltimoEstabelecimentoAcessado = primeiroEstabelecimento == Guid.Empty ? (Guid?)null : primeiroEstabelecimento
            }, transaction);

            const string sqlUsuarioEmpresa = @"
INSERT INTO usuario_empresas (
    id, id_usuario, id_empresa, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EmpresaId, @TipoAcesso, 'ativo', @CreatedByUserId, @CreatedByUserId,
    NOW(), NOW(), TRUE, NOW(), NOW())";

            await connection.ExecuteAsync(sqlUsuarioEmpresa, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EmpresaId = command.EmpresaId,
                TipoAcesso = command.CompanyRole,
                command.CreatedByUserId
            }, transaction);

            const string sqlUsuarioEstabelecimento = @"
INSERT INTO usuario_estabelecimentos (
    id, id_usuario, id_estabelecimento, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, permissoes_customizadas, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EstabelecimentoId, @TipoAcesso, 'ativo', @CreatedByUserId, @CreatedByUserId,
    NOW(), NOW(), NULL, TRUE, NOW(), NOW())";

            foreach (var estabelecimentoId in command.EstabelecimentoIds)
            {
                await connection.ExecuteAsync(sqlUsuarioEstabelecimento, new
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EstabelecimentoId = estabelecimentoId,
                    TipoAcesso = command.EstablishmentRole,
                    command.CreatedByUserId
                }, transaction);

                if (string.Equals(command.EstablishmentRole, "motoboy", StringComparison.OrdinalIgnoreCase))
                {
                    await EnsureMotoboyProfileAsync(connection, transaction, userId, estabelecimentoId, command.Nome);
                }
            }

            const string sqlSummary = @"
SELECT  emp.nome_fantasia       AS EmpresaNome,
        NULLIF(uemp.tipo_acesso, 'colaborador') AS CompanyRole,
        e.id                    AS Id,
        e.nome_fantasia         AS Nome,
        te.nome                 AS TipoEstabelecimento,
        ue.tipo_acesso          AS TipoAcesso
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN empresas emp ON emp.id = e.id_empresa
  JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
  LEFT JOIN usuario_empresas uemp
         ON uemp.id_usuario = ue.id_usuario
        AND uemp.id_empresa = e.id_empresa
        AND uemp.ativo = TRUE
        AND uemp.status = 'ativo'
 WHERE ue.id_usuario = @UserId
 ORDER BY e.nome_fantasia";

            var establishmentRows = await connection.QueryAsync<AdminCreatedUserEstabelecimento>(sqlSummary, new { UserId = userId }, transaction);
            const string sqlEmpresa = "SELECT nome_fantasia FROM empresas WHERE id = @EmpresaId LIMIT 1";
            var empresaNome = await connection.ExecuteScalarAsync<string>(sqlEmpresa, new { command.EmpresaId }, transaction);
            await transaction.CommitAsync();

            return new AdminCreatedUserSummary
            {
                Id = userId,
                Nome = command.Nome,
                Email = command.Email,
                Status = "ativo",
                EmpresaId = command.EmpresaId,
                EmpresaNome = empresaNome ?? string.Empty,
                CompanyRole = command.CompanyRole == "colaborador" ? null : command.CompanyRole,
                Estabelecimentos = establishmentRows.ToList()
            };
        }

        private static async Task EnsureMotoboyProfileAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int userId,
            Guid estabelecimentoId,
            string nome)
        {
            if (estabelecimentoId == Guid.Empty)
            {
                return;
            }

            var normalizedName = string.IsNullOrWhiteSpace(nome) ? "Motoboy" : nome.Trim();
            var motoboyId = await connection.ExecuteScalarAsync<int?>(@"
SELECT canonical_motoboy_id
  FROM motoboy
 WHERE id_usuario = @UserId
 ORDER BY CASE WHEN canonical_motoboy_id = id THEN 0 ELSE 1 END, id
 LIMIT 1
 FOR UPDATE;", new { UserId = userId }, transaction);

            if (!motoboyId.HasValue)
            {
                motoboyId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO motoboy (nome, status, id_usuario, id_estabelecimento, is_simulated)
VALUES (@Nome, 2, @UserId, @EstabelecimentoId, FALSE)
RETURNING id;", new
                {
                    Nome = normalizedName,
                    UserId = userId,
                    EstabelecimentoId = estabelecimentoId
                }, transaction);
            }

            await connection.ExecuteAsync(@"
UPDATE motoboy
   SET canonical_motoboy_id = id,
       nome = @Nome,
       id_estabelecimento = COALESCE(id_estabelecimento, @EstabelecimentoId),
       is_simulated = FALSE
 WHERE id = @MotoboyId;

INSERT INTO motoboy_estabelecimento (
    motoboy_id, estabelecimento_id, ativo, simulator_enabled,
    created_at_utc, updated_at_utc, disabled_at_utc)
VALUES (@MotoboyId, @EstabelecimentoId, TRUE, FALSE, NOW(), NOW(), NULL)
ON CONFLICT (motoboy_id, estabelecimento_id) DO UPDATE
   SET ativo = TRUE,
       simulator_enabled = FALSE,
       updated_at_utc = NOW(),
       disabled_at_utc = NULL;", new
            {
                MotoboyId = motoboyId.Value,
                Nome = normalizedName,
                UserId = userId,
                EstabelecimentoId = estabelecimentoId
            }, transaction);
        }
    }
}
