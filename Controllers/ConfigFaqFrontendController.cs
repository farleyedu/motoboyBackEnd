using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/{estabelecimentoId:guid}/faq")]
    public class ConfigFaqFrontendController : EstabelecimentoScopedControllerBase
    {
        private readonly IEstabelecimentoFaqService _service;

        public ConfigFaqFrontendController(IEstabelecimentoFaqService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Listar(Guid estabelecimentoId)
        {
            if (!TryResolveAndValidate("FAQ", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var paged = await _service.ListarAsync(effectiveId, null, null, null, null, 1, 500);
            return Ok(ApiResponse<IReadOnlyCollection<EstabelecimentoFaqDto>>.Ok(paged.Itens));
        }

        [HttpPost]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Criar(Guid estabelecimentoId, [FromBody] SalvarEstabelecimentoFaqRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("FAQ", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            try
            {
                var result = await _service.CriarAsync(effectiveId, request);
                return Ok(ApiResponse<EstabelecimentoFaqDto>.Ok(result));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        [HttpPut("{id:guid}")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Atualizar(Guid estabelecimentoId, Guid id, [FromBody] SalvarEstabelecimentoFaqRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("FAQ", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            try
            {
                var result = await _service.AtualizarAsync(effectiveId, id, request);
                return Ok(ApiResponse<EstabelecimentoFaqDto>.Ok(result));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpDelete("{id:guid}")]
        [RequirePermission("Configuracoes", "deletar")]
        public async Task<IActionResult> Excluir(Guid estabelecimentoId, Guid id)
        {
            if (!TryResolveAndValidate("FAQ", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var removed = await _service.ExcluirAsync(effectiveId, id);
            return removed
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
        }

        [HttpPatch("{id:guid}/ativo")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> AtualizarAtivo(Guid estabelecimentoId, Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("FAQ", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var updated = await _service.AtualizarStatusAsync(effectiveId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
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
