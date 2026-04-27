using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;

namespace APIBack.Middleware
{
    public class JwtAuthenticationMiddleware
    {
        private readonly RequestDelegate _next;

        public JwtAuthenticationMiddleware(RequestDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(HttpContext context, IJwtService jwtService)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var authorizationHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            var queryToken = context.Request.Query["access_token"].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(authorizationHeader) &&
                authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var rawToken = authorizationHeader["Bearer ".Length..].Trim();
                await TryAttachPayloadAsync(context, jwtService, rawToken);
            }
            else if (!string.IsNullOrWhiteSpace(queryToken) &&
                     context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            {
                await TryAttachPayloadAsync(context, jwtService, queryToken);
            }

            await _next(context);
        }

        private static Task TryAttachPayloadAsync(HttpContext context, IJwtService jwtService, string rawToken)
        {
            var token = ExtractJwt(rawToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                return Task.CompletedTask;
            }

            try
            {
                var payload = jwtService.ValidateToken(token);

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

            return Task.CompletedTask;
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
