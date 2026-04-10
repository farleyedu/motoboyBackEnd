using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    public abstract class CrmControllerBase : ApiControllerBase
    {
        protected int? CurrentUserId => HttpContext.GetUserId();
        protected bool CurrentIsSuperAdmin => HttpContext.IsSuperAdmin();

        protected IActionResult UnauthorizedResponse()
        {
            return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
        }

        protected IActionResult ForbiddenResponse()
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("Acesso negado."));
        }

        protected IActionResult? EnsureCrmAccess()
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!CurrentIsSuperAdmin)
            {
                return ForbiddenResponse();
            }

            return null;
        }
    }
}
