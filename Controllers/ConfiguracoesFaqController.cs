using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/faqs")]
    public class ConfiguracoesFaqController : EstabelecimentoScopedControllerBase
    {
        private readonly IEstabelecimentoFaqService _service;

        public ConfiguracoesFaqController(IEstabelecimentoFaqService service)
        {
            _service = service;
        }

        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] string? busca = null,
            [FromQuery] bool? ativo = null,
            [FromQuery] string? categoria = null,
            [FromQuery] string? acao = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarAsync(estabelecimentoId, busca, ativo, categoria, acao, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<EstabelecimentoFaqDto>>.Ok(response));
        }

        [HttpGet("{id:guid}")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Obter(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterPorIdAsync(estabelecimentoId, id);
            return response == null
                ? NotFoundErrorResponse("Registro nao encontrado.")
                : Ok(ApiResponse<EstabelecimentoFaqDto>.Ok(response));
        }

        [HttpPost]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Criar([FromBody] SalvarEstabelecimentoFaqRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.CriarAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(Obter), new { id = response.Id }, ApiResponse<EstabelecimentoFaqDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        [HttpPut("{id:guid}")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Atualizar(Guid id, [FromBody] SalvarEstabelecimentoFaqRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.AtualizarAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<EstabelecimentoFaqDto>.Ok(response));
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

        [HttpPatch("{id:guid}/status")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> AtualizarStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarStatusAsync(estabelecimentoId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
        }

        [HttpDelete("{id:guid}")]
        [RequirePermission("Configuracoes", "deletar")]
        public async Task<IActionResult> Excluir(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("FAQ", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var removed = await _service.ExcluirAsync(estabelecimentoId, id);
            return removed
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
        }
    }
}
