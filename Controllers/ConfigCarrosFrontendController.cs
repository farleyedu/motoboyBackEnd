using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/{estabelecimentoId:guid}/carros")]
    public class ConfigCarrosFrontendController : EstabelecimentoScopedControllerBase
    {
        private readonly IConfiguracaoCarroService _service;

        public ConfigCarrosFrontendController(IConfiguracaoCarroService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Listar(Guid estabelecimentoId)
        {
            if (!TryResolveAndValidate("Servicos", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var itens = await _service.ListarPorEstabelecimentoAsync(effectiveId);
            return Ok(ApiResponse<IReadOnlyCollection<CarroEstabelecimentoDto>>.Ok(itens));
        }

        private bool TryResolveAndValidate(string module, Guid routeId, out Guid effectiveId, out IActionResult? error)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule(module, out effectiveId, out error))
                return false;

            if (effectiveId != routeId && !CurrentIsSuperAdmin)
            {
                error = StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("Acesso negado ao estabelecimento informado."));
                effectiveId = Guid.Empty;
                return false;
            }

            if (CurrentIsSuperAdmin)
                effectiveId = routeId;

            return true;
        }
    }
}
