using System;
using System.Linq;
using APIBack.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace APIBack.Attributes
{
    /// <summary>
    /// Exige um modulo comercial ligado no estabelecimento do token (nome da UI: "Simulador", "Remuneracao", "iFood"...).
    /// Desligar um modulo precisa valer no backend, nao so esconder o menu (plano de modulos, 3.3). Super admin passa.
    /// Os modulos vem do banco a cada requisicao (JwtAuthenticationMiddleware), nao de um token antigo.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequireModuleAttribute : Attribute, IAuthorizationFilter
    {
        public RequireModuleAttribute(string modulo) => Modulo = modulo ?? string.Empty;

        public string Modulo { get; }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (context.Filters.Any(f => f is IAllowAnonymousFilter)) return;
            if (context.HttpContext.GetEndpoint()?.Metadata?.GetMetadata<IAllowAnonymous>() != null) return;
            if (context.HttpContext.IsSuperAdmin()) return;

            var ativos = context.HttpContext.GetEstabelecimentoModulosAtivos();
            if (ativos.Any(m => string.Equals(m, Modulo, StringComparison.OrdinalIgnoreCase))) return;

            context.Result = new JsonResult(new
            {
                success = false,
                error = "Este recurso nao faz parte do plano da loja.",
                code = "MODULO_INATIVO",
                modulo = Modulo,
                details = APIBack.Service.ErrosOperacionais.Detalhes(APIBack.Service.ErrosOperacionais.ModuloInativo,
                    dados: new System.Collections.Generic.Dictionary<string, object?> { ["modulo"] = Modulo })
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
