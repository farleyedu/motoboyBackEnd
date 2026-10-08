using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Middleware
{
    public class JwtAuthenticationMiddleware
    {
        private readonly RequestDelegate _next;

        public JwtAuthenticationMiddleware(RequestDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(HttpContext context, IJwtService jwtService, IConfiguration configuration)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var authorizationHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            var queryToken = context.Request.Query["access_token"].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(authorizationHeader) &&
                authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var rawToken = authorizationHeader["Bearer ".Length..].Trim();
                await TryAttachPayloadAsync(context, jwtService, configuration, rawToken);
            }
            else if (!string.IsNullOrWhiteSpace(queryToken) &&
                     context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                await TryAttachPayloadAsync(context, jwtService, configuration, queryToken);
            }

            await _next(context);
        }

        private static async Task TryAttachPayloadAsync(HttpContext context, IJwtService jwtService, IConfiguration configuration, string rawToken)
        {
            var token = ExtractJwt(rawToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            JwtPayload payload;
            try
            {
                payload = jwtService.ValidateToken(token);
            }
            catch
            {
                // Apenas a validacao criptografica pode tratar a falha como token invalido.
                return;
            }

            // Resolve permissions from the current membership on every request: old JWTs
            // must not keep revoked grants or access to a disabled membership.
            if (!payload.IsSuperAdmin && !payload.MotoboySessionId.HasValue && payload.VinculoId.HasValue)
            {
                var repository = context.RequestServices.GetRequiredService<APIBack.Automation.Repository.Interface.IEstabelecimentoSelectionRepository>();
                var membership = await repository.ObterVinculoPorIdAsync(payload.VinculoId.Value);
                var user = payload.UserId.HasValue ? await repository.ObterUsuarioAsync(payload.UserId.Value) : null;
                if (membership == null || user == null || !user.IsAtivo ||
                    membership.UsuarioId != payload.UserId || membership.EstabelecimentoId != payload.EstabelecimentoId ||
                    membership.VinculoAtivo == false || !string.Equals(membership.Status, "ativo", StringComparison.OrdinalIgnoreCase))
                    return;
                var establishment = await repository.ObterEstabelecimentoDetalheAsync(membership.EstabelecimentoId);
                if (establishment == null || establishment.Ativo == false) return;
                payload.TipoAcesso = APIBack.Security.RoleCatalog.Normalize(membership.TipoAcesso);
                payload.EstabelecimentoNome = establishment.Nome;
                payload.EstabelecimentoModulosAtivos = APIBack.Model.Gestao.EstabelecimentoModuleMapper.ToUiModules(establishment.Nome, establishment.ModulosAtivosRaw);
                payload.Permissoes = APIBack.Security.EstablishmentPermissions.Resolve(membership.PermissoesCustomizadas, establishment.ModulosAtivosRaw);
            }

            var allowEndedOperationalSession =
                HttpMethods.IsDelete(context.Request.Method) &&
                string.Equals(
                    context.Request.Path.Value,
                    "/api/v2/motoboys/me/session",
                    StringComparison.OrdinalIgnoreCase);

            context.RequestAborted.ThrowIfCancellationRequested();
            if (!await IsPayloadStillAllowedAsync(payload, context, allowEndedOperationalSession))
            {
                context.Items["AuthenticationFailureCode"] = "TOKEN_CONTEXT_REJECTED";
                return;
            }

            context.Items["JwtPayload"] = payload;
            context.Items["UserId"] = payload.UserId;
            context.Items["UserEmail"] = payload.Email;
            context.Items["UserNome"] = payload.Nome;
            context.Items["IsSuperAdmin"] = payload.IsSuperAdmin;
            context.Items["EmpresaId"] = payload.EmpresaId;
            context.Items["EmpresaNome"] = payload.EmpresaNome;
            context.Items["TipoAcessoEmpresa"] = payload.TipoAcessoEmpresa;
            context.Items["EmpresaVinculoId"] = payload.EmpresaVinculoId;
            context.Items["EstabelecimentoId"] = payload.EstabelecimentoId;
            context.Items["EstabelecimentoNome"] = payload.EstabelecimentoNome;
            context.Items["TipoEstabelecimento"] = payload.TipoEstabelecimento;
            context.Items["EstabelecimentoModulosAtivos"] = payload.EstabelecimentoModulosAtivos;
            context.Items["TipoAcesso"] = payload.TipoAcesso;
            context.Items["VinculoId"] = payload.VinculoId;
            context.Items["Permissoes"] = payload.Permissoes;

        }

        private static async Task<bool> IsPayloadStillAllowedAsync(
            JwtPayload payload,
            HttpContext context,
            bool allowEndedOperationalSession)
        {
            if (payload.MotoboySessionId.HasValue)
            {
                var sessionAllowed = await IsMotoboySessionAllowedAsync(
                    payload,
                    context,
                    allowEndedOperationalSession);
                if (!sessionAllowed)
                {
                    return false;
                }
            }

            if (payload.IsSuperAdmin || !payload.EmpresaId.HasValue)
            {
                return true;
            }

            var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync(context.RequestAborted);
            var allowed = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(@"
SELECT COALESCE(emp.ativo, TRUE) = TRUE
   AND COALESCE(emp.pausada, FALSE) = FALSE
   AND (
    @EstabelecimentoId IS NULL
    OR EXISTS (
        SELECT 1
          FROM estabelecimentos e
         WHERE e.id = @EstabelecimentoId
           AND e.id_empresa = emp.id
           AND COALESCE(e.ativo, TRUE) = TRUE
           AND COALESCE(e.status, 'ativo') IN ('ativo', 'trial')
    )
   )
  FROM empresas emp
 WHERE emp.id = @EmpresaId
 LIMIT 1;",
                new
                {
                    EmpresaId = payload.EmpresaId,
                    EstabelecimentoId = payload.EstabelecimentoId
                }, commandTimeout: 10, cancellationToken: context.RequestAborted));

            return allowed == true;
        }

        private static async Task<bool> IsMotoboySessionAllowedAsync(
            JwtPayload payload,
            HttpContext context,
            bool allowEndedOperationalSession)
        {
            if (!payload.MotoboySessionId.HasValue || !payload.MotoboyId.HasValue)
            {
                return true;
            }

            var dataSource = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync(context.RequestAborted);
            var active = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(@"
SELECT TRUE
  FROM motoboy_active_sessions s
 WHERE s.session_id = @SessionId
   AND s.motoboy_id = COALESCE(
   (SELECT m.canonical_motoboy_id FROM motoboy m WHERE m.id = @MotoboyId),
   @MotoboyId)
   AND (@SessionEpoch IS NULL OR s.session_epoch = @SessionEpoch)
   AND (@EstabelecimentoId IS NULL OR s.id_estabelecimento = @EstabelecimentoId)
   AND (
   @AllowEndedOperationalSession = TRUE
   OR (
       s.ended_at_utc IS NULL
       AND s.revoked_at IS NULL
       AND s.expires_at_utc > NOW()
       AND EXISTS (
           SELECT 1
             FROM motoboy_estabelecimento me
             JOIN estabelecimentos e ON e.id = me.estabelecimento_id
             JOIN motoboy m ON m.id = me.motoboy_id
            WHERE me.motoboy_id = s.motoboy_id
              AND me.estabelecimento_id = s.id_estabelecimento
              AND me.ativo = TRUE
              AND COALESCE(e.ativo, TRUE) = TRUE
              AND LOWER(COALESCE(e.status, 'ativo')) IN ('ativo', 'trial')
              AND (
                  (s.origin = 'simulator' AND me.simulator_enabled = TRUE AND m.is_simulated = TRUE)
                  OR (
                      s.origin = 'mobile'
                      AND EXISTS (
                          SELECT 1
                            FROM usuario_estabelecimentos ue
                           WHERE ue.id_usuario = s.id_usuario
                             AND ue.id_estabelecimento = s.id_estabelecimento
                             AND LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
                             AND COALESCE(ue.ativo, TRUE) = TRUE
                             AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
                      )
                  )
              )
       )
   )
   )
 LIMIT 1;",
                new
                {
                    SessionId = payload.MotoboySessionId,
                    MotoboyId = payload.MotoboyId,
                    SessionEpoch = payload.SessionEpoch,
                    EstabelecimentoId = payload.EstabelecimentoId,
                    AllowEndedOperationalSession = allowEndedOperationalSession
                }, commandTimeout: 10, cancellationToken: context.RequestAborted));

            return active == true;
        }

        private static string ExtractJwt(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return string.Empty;
            }

            // Be tolerant with malformed inputs copied from tools (e.g. trailing JSON fragments).
            // Valid JWT chars are base64url + dots.
            var validChars = rawToken
                .TakeWhile(ch =>
                    (ch >= 'a' && ch <= 'z') ||
                    (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9') ||
                    ch == '-' ||
                    ch == '_' ||
                    ch == '.')
                .ToArray();

            return new string(validChars);
        }
    }
}
