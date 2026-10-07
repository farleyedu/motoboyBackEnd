using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Motoboy;
using APIBack.Extensions;
using APIBack.Hubs;
using APIBack.Security;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

using CustomAuthorize = APIBack.Attributes.AuthorizeAttribute;

namespace APIBack.Controllers
{
    [Route("api/motoboys")]
    public sealed class MotoboyOnboardingController : ControllerBase
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly IHubContext<DeliveryHub> _hub;

        public MotoboyOnboardingController(NpgsqlDataSource dataSource, IHubContext<DeliveryHub> hub)
        {
            _dataSource = dataSource;
            _hub = hub;
        }

        [HttpPost("cadastro")]
        [AllowAnonymous]
        public async Task<IActionResult> Cadastrar([FromBody] MotoboyCadastroRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Informe nome, e-mail e uma senha com pelo menos 6 caracteres."));
            }

            var nome = request.Nome.Trim();
            var email = request.Email.Trim().ToLowerInvariant();
            var telefone = string.IsNullOrWhiteSpace(request.Telefone) ? null : request.Telefone.Trim();

            if (nome.Length < 2 || string.IsNullOrWhiteSpace(email) || request.Senha.Trim().Length < 6)
            {
                return BadRequest(ApiResponse<object>.Fail("Informe nome, e-mail e uma senha com pelo menos 6 caracteres."));
            }

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var emailExists = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM usuario
     WHERE LOWER(email) = @Email AND deleted_at IS NULL
);", new { Email = email }, transaction);

            if (emailExists)
            {
                return Conflict(ApiResponse<object>.Fail("Este e-mail já está cadastrado.", "EMAIL_ALREADY_EXISTS"));
            }

            var userId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO usuario (nome, email, senha, is_super_admin, provider, created_at, updated_at)
VALUES (@Nome, @Email, @Senha, FALSE, 'local', NOW(), NOW())
RETURNING id;", new
            {
                Nome = nome,
                Email = email,
                Senha = PasswordSecurity.Hash(request.Senha)
            }, transaction);

            var motoboyId = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO motoboy (nome, telefone, status, id_usuario, is_simulated, status_cadastro)
VALUES (@Nome, @Telefone, 2, @UserId, FALSE, 'ativo')
RETURNING id;", new
            {
                Nome = nome,
                Telefone = telefone,
                UserId = userId
            }, transaction);

            await transaction.CommitAsync();

            return StatusCode(StatusCodes.Status201Created, ApiResponse<MotoboyCadastroResponse>.Ok(new MotoboyCadastroResponse
            {
                UserId = userId,
                MotoboyId = motoboyId,
                Nome = nome,
                Email = email
            }));
        }

        [HttpGet("me/estabelecimentos-disponiveis")]
        [CustomAuthorize]
        public async Task<IActionResult> ListarEstabelecimentosDisponiveis()
        {
            var rows = await QueryAsync<MotoboyEstabelecimentoDisponivelDto>(@"
SELECT e.id AS Id,
       e.nome_fantasia AS Nome,
       e.cidade AS Cidade,
       e.uf AS Uf,
       te.nome AS TipoEstabelecimento,
       COALESCE(e.modulos_ativos::text[], ARRAY[]::text[]) AS ModulosAtivos
  FROM estabelecimentos e
  LEFT JOIN tipo_estabelecimento te ON te.id = e.id_tipo_estabelecimento
 WHERE COALESCE(e.ativo, TRUE) = TRUE
   AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
   AND e.modulos_ativos @> ARRAY['DELIVERY']::modulo_enum[]
 ORDER BY e.nome_fantasia;");

            return Ok(ApiResponse<IReadOnlyCollection<MotoboyEstabelecimentoDisponivelDto>>.Ok(rows));
        }

        [HttpGet("me/vinculos/solicitacoes")]
        [CustomAuthorize]
        public async Task<IActionResult> MinhasSolicitacoes()
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue) return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));

            var rows = await QueryAsync<MotoboyLinkRequestDto>(@"
SELECT r.id AS Id,
       r.motoboy_id AS MotoboyId,
       r.estabelecimento_id AS EstabelecimentoId,
       e.nome_fantasia AS EstabelecimentoNome,
       r.status AS Status,
       r.requested_at_utc AS RequestedAtUtc,
       r.reviewed_at_utc AS ReviewedAtUtc,
       r.rejection_reason AS RejectionReason,
       m.nome AS MotoboyNome,
       u.email AS MotoboyEmail,
       m.telefone AS MotoboyTelefone
  FROM motoboy_link_requests r
  JOIN motoboy m ON m.id = r.motoboy_id
  JOIN usuario u ON u.id = m.id_usuario
  JOIN estabelecimentos e ON e.id = r.estabelecimento_id
 WHERE m.id_usuario = @UserId
 ORDER BY r.requested_at_utc DESC;", new { UserId = userId.Value });

            return Ok(ApiResponse<IReadOnlyCollection<MotoboyLinkRequestDto>>.Ok(rows));
        }

        [HttpPost("me/vinculos/solicitar")]
        [CustomAuthorize]
        public async Task<IActionResult> SolicitarVinculo([FromBody] SolicitarVinculoMotoboyRequest? request)
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue) return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));
            if (request == null || request.EstabelecimentoId == Guid.Empty)
                return BadRequest(ApiResponse<object>.Fail("Informe um restaurante válido."));

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var motoboy = await connection.QuerySingleOrDefaultAsync<MotoboyIdentityRow>(@"
SELECT id AS Id, nome AS Nome
  FROM motoboy
 WHERE id_usuario = @UserId AND canonical_motoboy_id = id
 LIMIT 1;", new { UserId = userId.Value }, transaction);

            // Contas antigas podem ter sido criadas antes do perfil estruturado de motoboy.
            // Regulariza o perfil no primeiro pedido de vínculo, sem criar vínculo ainda.
            if (motoboy == null || motoboy.Id <= 0)
            {
                var user = await connection.QuerySingleOrDefaultAsync<MotoboyUserRow>(@"
SELECT id AS Id, nome AS Nome
  FROM usuario
 WHERE id = @UserId AND deleted_at IS NULL
 LIMIT 1;", new { UserId = userId.Value }, transaction);

                if (user == null || user.Id <= 0)
                {
                    return BadRequest(ApiResponse<object>.Fail("Usuário não encontrado."));
                }

                motoboy = await connection.QuerySingleAsync<MotoboyIdentityRow>(@"
INSERT INTO motoboy (nome, status, id_usuario, is_simulated, status_cadastro)
VALUES (@Nome, 2, @UserId, FALSE, 'ativo')
RETURNING id AS Id, nome AS Nome;", new { user.Nome, UserId = user.Id }, transaction);
            }

            var estabelecimentoExists = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM estabelecimentos
     WHERE id = @EstabelecimentoId
       AND COALESCE(ativo, TRUE) = TRUE
       AND COALESCE(status, 'ativo') IN ('ativo', 'trial')
);", new { request.EstabelecimentoId }, transaction);

            if (!estabelecimentoExists)
            {
                return NotFound(ApiResponse<object>.Fail("Restaurante não encontrado ou indisponível."));
            }

            var alreadyLinked = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
    SELECT 1 FROM motoboy_estabelecimento
     WHERE motoboy_id = @MotoboyId AND estabelecimento_id = @EstabelecimentoId AND ativo = TRUE
);", new { MotoboyId = motoboy.Id, request.EstabelecimentoId }, transaction);

            if (alreadyLinked)
            {
                return Conflict(ApiResponse<object>.Fail("Você já está vinculado a este restaurante.", "ALREADY_LINKED"));
            }

            var existing = await connection.QuerySingleOrDefaultAsync<MotoboyLinkRequestDto>(@"
SELECT r.id AS Id,
       r.motoboy_id AS MotoboyId,
       r.estabelecimento_id AS EstabelecimentoId,
       e.nome_fantasia AS EstabelecimentoNome,
       r.status AS Status,
       r.requested_at_utc AS RequestedAtUtc,
       r.reviewed_at_utc AS ReviewedAtUtc,
       r.rejection_reason AS RejectionReason
  FROM motoboy_link_requests r
  JOIN estabelecimentos e ON e.id = r.estabelecimento_id
 WHERE r.motoboy_id = @MotoboyId
   AND r.estabelecimento_id = @EstabelecimentoId
   AND r.status = 'pending';", new { MotoboyId = motoboy.Id, request.EstabelecimentoId }, transaction);

            if (existing != null)
            {
                await transaction.CommitAsync();
                return Ok(ApiResponse<MotoboyLinkRequestDto>.Ok(existing));
            }

            var created = await connection.QuerySingleAsync<MotoboyLinkRequestDto>(@"
INSERT INTO motoboy_link_requests (motoboy_id, estabelecimento_id, status)
VALUES (@MotoboyId, @EstabelecimentoId, 'pending')
RETURNING id AS Id,
          motoboy_id AS MotoboyId,
          estabelecimento_id AS EstabelecimentoId,
          (SELECT nome_fantasia FROM estabelecimentos WHERE id = estabelecimento_id) AS EstabelecimentoNome,
          status AS Status,
          requested_at_utc AS RequestedAtUtc,
          reviewed_at_utc AS ReviewedAtUtc,
          rejection_reason AS RejectionReason;", new { MotoboyId = motoboy.Id, request.EstabelecimentoId }, transaction);

            await transaction.CommitAsync();

            await _hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(request.EstabelecimentoId))
                .SendAsync(DeliveryRealtimeEvents.MotoboyLinkRequested, new
                {
                    requestId = created.Id,
                    motoboyId = motoboy.Id,
                    motoboyNome = motoboy.Nome,
                    estabelecimentoId = request.EstabelecimentoId,
                    created.RequestedAtUtc
                });

            return StatusCode(StatusCodes.Status201Created, ApiResponse<MotoboyLinkRequestDto>.Ok(created));
        }

        [HttpGet("solicitacoes")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> ListarSolicitacoesDoRestaurante()
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            if (!estabelecimentoId.HasValue || estabelecimentoId.Value == Guid.Empty)
                return BadRequest(ApiResponse<object>.Fail("Selecione um restaurante."));

            var rows = await QueryAsync<MotoboyLinkRequestDto>(@"
SELECT r.id AS Id,
       r.motoboy_id AS MotoboyId,
       r.estabelecimento_id AS EstabelecimentoId,
       e.nome_fantasia AS EstabelecimentoNome,
       r.status AS Status,
       r.requested_at_utc AS RequestedAtUtc,
       r.reviewed_at_utc AS ReviewedAtUtc,
       r.rejection_reason AS RejectionReason,
       m.nome AS MotoboyNome,
       u.email AS MotoboyEmail,
       m.telefone AS MotoboyTelefone
  FROM motoboy_link_requests r
  JOIN motoboy m ON m.id = r.motoboy_id
  JOIN usuario u ON u.id = m.id_usuario
  JOIN estabelecimentos e ON e.id = r.estabelecimento_id
 WHERE r.estabelecimento_id = @EstabelecimentoId
 ORDER BY CASE WHEN r.status = 'pending' THEN 0 ELSE 1 END, r.requested_at_utc DESC;", new { EstabelecimentoId = estabelecimentoId.Value });

            return Ok(ApiResponse<IReadOnlyCollection<MotoboyLinkRequestDto>>.Ok(rows));
        }

        [HttpPost("solicitacoes/{requestId:guid}/aprovar")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> AprovarSolicitacao(Guid requestId)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            var actorUserId = HttpContext.GetUserId();
            if (!estabelecimentoId.HasValue || !actorUserId.HasValue)
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado inválido."));

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var row = await connection.QuerySingleOrDefaultAsync<PendingLinkApprovalRow>(@"
SELECT r.motoboy_id AS MotoboyId,
       m.id_usuario AS MotoboyUserId,
       e.id_empresa AS EmpresaId
  FROM motoboy_link_requests r
  JOIN motoboy m ON m.id = r.motoboy_id
  JOIN estabelecimentos e ON e.id = r.estabelecimento_id
 WHERE r.id = @RequestId
   AND r.estabelecimento_id = @EstabelecimentoId
   AND r.status = 'pending'
 FOR UPDATE;", new { RequestId = requestId, EstabelecimentoId = estabelecimentoId.Value }, transaction);

            if (row == null || row.MotoboyId <= 0)
                return NotFound(ApiResponse<object>.Fail("Solicitação pendente não encontrada."));

            await connection.ExecuteAsync(@"
INSERT INTO usuario_empresas (
    id, id_usuario, id_empresa, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, ativo, created_at, updated_at)
VALUES (gen_random_uuid(), @UserId, @EmpresaId, 'colaborador', 'ativo', @Actor, @Actor,
        NOW(), NOW(), TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_empresa) DO UPDATE
   SET status = 'ativo', ativo = TRUE, aprovado_por = EXCLUDED.aprovado_por,
       data_aprovacao = NOW(), data_remocao = NULL, updated_at = NOW();

INSERT INTO usuario_estabelecimentos (
    id, id_usuario, id_estabelecimento, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, permissoes_customizadas, ativo, created_at, updated_at)
VALUES (gen_random_uuid(), @UserId, @EstabelecimentoId, 'motoboy', 'ativo', @Actor, @Actor,
        NOW(), NOW(), NULL, TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_estabelecimento) DO UPDATE
   SET tipo_acesso = 'motoboy', status = 'ativo', ativo = TRUE, aprovado_por = EXCLUDED.aprovado_por,
       data_aprovacao = NOW(), data_remocao = NULL, updated_at = NOW();

INSERT INTO motoboy_estabelecimento (motoboy_id, estabelecimento_id, ativo, simulator_enabled)
VALUES (@MotoboyId, @EstabelecimentoId, TRUE, FALSE)
ON CONFLICT (motoboy_id, estabelecimento_id) DO UPDATE
   SET ativo = TRUE, disabled_at_utc = NULL, updated_at_utc = NOW(), simulator_enabled = FALSE;

UPDATE motoboy_link_requests
   SET status = 'approved', reviewed_at_utc = NOW(), reviewed_by_user_id = @Actor,
       rejection_reason = NULL
 WHERE id = @RequestId;", new
            {
                UserId = row.MotoboyUserId,
                row.EmpresaId,
                EstablishmentId = estabelecimentoId.Value,
                MotoboyId = row.MotoboyId,
                Actor = actorUserId.Value,
                RequestId = requestId
            }, transaction);

            await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Ok(new { requestId, status = "approved" }));
        }

        [HttpPost("solicitacoes/{requestId:guid}/recusar")]
        [RequirePermission("Delivery", "gestao_motoboy")]
        public async Task<IActionResult> RecusarSolicitacao(Guid requestId, [FromBody] RecusarVinculoMotoboyRequest? request)
        {
            var estabelecimentoId = HttpContext.GetEstabelecimentoId();
            var actorUserId = HttpContext.GetUserId();
            if (!estabelecimentoId.HasValue || !actorUserId.HasValue)
                return Unauthorized(ApiResponse<object>.Fail("Contexto autenticado inválido."));

            await using var connection = await _dataSource.OpenConnectionAsync();
            var updated = await connection.ExecuteAsync(@"
UPDATE motoboy_link_requests
   SET status = 'rejected', reviewed_at_utc = NOW(), reviewed_by_user_id = @Actor,
       rejection_reason = @Motivo
 WHERE id = @RequestId
   AND estabelecimento_id = @EstabelecimentoId
   AND status = 'pending';", new
            {
                RequestId = requestId,
                EstabelecimentoId = estabelecimentoId.Value,
                Actor = actorUserId.Value,
                Motivo = string.IsNullOrWhiteSpace(request?.Motivo) ? null : request.Motivo.Trim()
            });

            if (updated == 0)
                return NotFound(ApiResponse<object>.Fail("Solicitação pendente não encontrada."));

            return Ok(ApiResponse<object>.Ok(new { requestId, status = "rejected" }));
        }

        private async Task<IReadOnlyCollection<T>> QueryAsync<T>(string sql, object? parameters = null)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<T>(sql, parameters);
            return rows.ToList();
        }
        private sealed class MotoboyIdentityRow
        {
            public int Id { get; set; }
            public string Nome { get; set; } = string.Empty;
        }

        private sealed class PendingLinkApprovalRow
        {
            public int MotoboyId { get; set; }
            public int MotoboyUserId { get; set; }
            public Guid EmpresaId { get; set; }
        }

        private sealed class MotoboyUserRow
        {
            public int Id { get; set; }
            public string Nome { get; set; } = string.Empty;
        }
    }
}
