using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    public abstract class GestaoControllerBase : ControllerBase
    {
        protected int? CurrentUserId => HttpContext.GetUserId();
        protected Guid? CurrentEmpresaId => HttpContext.GetEmpresaId();
        protected Guid? CurrentEstabelecimentoId => HttpContext.GetEstabelecimentoId();
        protected string CurrentCompanyRole => HttpContext.GetTipoAcessoEmpresa();
        protected string CurrentEstablishmentRole => HttpContext.GetTipoAcesso();
        protected bool CurrentIsSuperAdmin => HttpContext.IsSuperAdmin();

        protected bool HasAnyPermission(params (string Modulo, string Acao)[] checks)
        {
            if (CurrentIsSuperAdmin)
            {
                return true;
            }

            foreach (var check in checks)
            {
                if (HttpContext.TemPermissao(check.Modulo, check.Acao))
                {
                    return true;
                }
            }

            return false;
        }

        protected IActionResult UnauthorizedResponse()
        {
            return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
        }

        protected IActionResult ForbiddenResponse()
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("Acesso negado."));
        }
    }
}
