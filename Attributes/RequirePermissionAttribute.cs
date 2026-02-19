using System;
using System.Linq;
using APIBack.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APIBack.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequirePermissionAttribute : Attribute, IAuthorizationFilter
    {
        private readonly string _modulo;
        private readonly string _acao;

        public RequirePermissionAttribute(string modulo, string acao)
        {
            _modulo = modulo ?? string.Empty;
            _acao = acao ?? string.Empty;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var logger = context.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger<RequirePermissionAttribute>();

            if (context.Filters.Any(f => f is IAllowAnonymousFilter))
            {
                return;
            }

            if (context.HttpContext.GetEndpoint()?.Metadata?.GetMetadata<IAllowAnonymous>() != null)
            {
                return;
            }

            var userId = context.HttpContext.GetUserId();
            if (!userId.HasValue || userId.Value <= 0)
            {
                logger?.LogWarning(
                    "Auth denied (401) for permission check. Module={Modulo} Action={Acao} Path={Path} Method={Method}",
                    _modulo, _acao, context.HttpContext.Request.Path, context.HttpContext.Request.Method);

                context.Result = new JsonResult(new
                {
                    success = false,
                    error = "Não autorizado."
                })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
                return;
            }

            if (context.HttpContext.IsSuperAdmin())
            {
                return;
            }

            if (context.HttpContext.TemPermissao(_modulo, _acao))
            {
                return;
            }

            logger?.LogWarning(
                "Permission denied (403). UserId={UserId} TipoAcesso={TipoAcesso} EstabelecimentoId={EstabelecimentoId} Module={Modulo} Action={Acao} Path={Path} Method={Method}",
                userId.Value,
                context.HttpContext.GetTipoAcesso(),
                context.HttpContext.GetEstabelecimentoId(),
                _modulo,
                _acao,
                context.HttpContext.Request.Path,
                context.HttpContext.Request.Method);

            context.Result = new JsonResult(new
            {
                success = false,
                error = "Acesso negado."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
