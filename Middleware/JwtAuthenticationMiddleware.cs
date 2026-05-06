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

            try
            {
                var payload = jwtService.ValidateToken(token);

                if (!await IsPayloadStillAllowedAsync(payload, configuration))
                {
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
            catch
            {
                // N\u00e3o bloquear requisi\u00e7\u00f5es com token inv\u00e1lido aqui.
                // A valida\u00e7\u00e3o \u00e9 responsabilidade dos endpoints com [Authorize].
            }

            return;
        }

        private static async Task<bool> IsPayloadStillAllowedAsync(JwtPayload payload, IConfiguration configuration)
        {
            if (payload.IsSuperAdmin || !payload.EmpresaId.HasValue)
            {
                return true;
            }

            var connectionString = configuration.GetConnectionString("DefaultConnection")
                                   ?? configuration["ConnectionStrings:DefaultConnection"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return true;
            }

            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                var allowed = await connection.ExecuteScalarAsync<bool?>(@"
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
                    });

                return allowed == true;
            }
            catch
            {
                return true;
            }
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
