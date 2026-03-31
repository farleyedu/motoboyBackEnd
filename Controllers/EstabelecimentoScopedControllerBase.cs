using System;
using System.Linq;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    public abstract class EstabelecimentoScopedControllerBase : GestaoControllerBase
    {
        protected bool TryResolveCurrentEstabelecimento(out Guid effectiveId, out IActionResult? error)
        {
            if (!CurrentUserId.HasValue || CurrentUserId.Value <= 0)
            {
                effectiveId = Guid.Empty;
                error = UnauthorizedResponse();
                return false;
            }

            if (!CurrentEstabelecimentoId.HasValue || CurrentEstabelecimentoId.Value == Guid.Empty)
            {
                effectiveId = Guid.Empty;
                error = Unauthorized(ApiResponse<object>.Fail("Sessao invalida para o estabelecimento atual."));
                return false;
            }

            effectiveId = CurrentEstabelecimentoId.Value;
            error = null;
            return true;
        }

        protected bool TryResolveCurrentEstabelecimentoAndModule(string requiredModule, out Guid effectiveId, out IActionResult? error)
        {
            if (!TryResolveCurrentEstabelecimento(out effectiveId, out error))
            {
                return false;
            }

            var normalizedRequired = NormalizeToken(requiredModule);
            var modules = HttpContext.GetEstabelecimentoModulosAtivos();
            var moduleActive = modules.Any(m => NormalizeToken(m) == normalizedRequired);

            if (moduleActive)
            {
                error = null;
                return true;
            }

            effectiveId = Guid.Empty;
            error = StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("Modulo inativo para o estabelecimento atual."));
            return false;
        }

        private static string NormalizeToken(string? value)
        {
            return ValidationUtils.NormalizeToken(value);
        }
    }
}
