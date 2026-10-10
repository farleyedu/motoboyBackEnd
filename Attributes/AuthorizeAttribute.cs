using System;
using System.Linq;
using APIBack.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace APIBack.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class AuthorizeAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // Respect [AllowAnonymous] on actions/controllers.
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
                context.Result = new JsonResult(new
                {
                    success = false,
                    error = context.HttpContext.Items["AuthenticationFailureCode"] as string == "SESSION_REPLACED"
                        ? "Esta conta foi acessada em outro aparelho. Entre novamente para continuar." : "N\u00e3o autorizado.",
                    code = context.HttpContext.Items["AuthenticationFailureCode"] as string
                })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
        }
    }
}
