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

            if (!string.IsNullOrWhiteSpace(authorizationHeader) &&
                authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authorizationHeader["Bearer ".Length..].Trim();

                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        var payload = jwtService.ValidateToken(token);

                        context.Items["JwtPayload"] = payload;
                        context.Items["UserId"] = payload.UserId;
                        context.Items["UserEmail"] = payload.Email;
                        context.Items["UserNome"] = payload.Nome;
                        context.Items["IsSuperAdmin"] = payload.IsSuperAdmin;
                        context.Items["EstabelecimentoId"] = payload.EstabelecimentoId;
                        context.Items["EstabelecimentoNome"] = payload.EstabelecimentoNome;
                        context.Items["TipoEstabelecimento"] = payload.TipoEstabelecimento;
                        context.Items["TipoAcesso"] = payload.TipoAcesso;
                        context.Items["VinculoId"] = payload.VinculoId;
                        context.Items["Permissoes"] = payload.Permissoes;
                    }
                    catch
                    {
                        // N\u00e3o bloquear requisi\u00e7\u00f5es com token inv\u00e1lido aqui.
                        // A valida\u00e7\u00e3o \u00e9 responsabilidade dos endpoints com [Authorize].
                    }
                }
            }

            await _next(context);
        }
    }
}

