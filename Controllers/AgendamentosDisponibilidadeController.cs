using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/disponibilidade")]
    public class AgendamentosDisponibilidadeController : EstabelecimentoScopedControllerBase
    {
        private readonly IAgendaDisponibilidadeService _service;

        public AgendamentosDisponibilidadeController(IAgendaDisponibilidadeService service)
        {
            _service = service;
        }

        [HttpGet("profissionais")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> ListarProfissionais()
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarProfissionaisAsync(estabelecimentoId);
            return Ok(ApiResponse<PagedResultDto<ProfissionalDisponibilidadeDto>>.Ok(response));
        }

        [HttpGet("regras")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Listar(
            [FromQuery] bool? ativo = null,
            [FromQuery] string? tipo = null,
            [FromQuery] string? escopo = null,
            [FromQuery] string? profissionalId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ListarAsync(estabelecimentoId, ativo, tipo, escopo, profissionalId, page, pageSize);
            return Ok(ApiResponse<PagedResultDto<AgendaDisponibilidadeDto>>.Ok(response));
        }

        [HttpGet("regras/{id:guid}")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> Obter(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var response = await _service.ObterPorIdAsync(estabelecimentoId, id);
            return response == null
                ? NotFoundErrorResponse("Registro nao encontrado.")
                : Ok(ApiResponse<AgendaDisponibilidadeDto>.Ok(response));
        }

        [HttpPost("regras")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Criar([FromBody] SalvarAgendaDisponibilidadeRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.CriarAsync(estabelecimentoId, request);
                return CreatedAtAction(nameof(Obter), new { id = response.Id }, ApiResponse<AgendaDisponibilidadeDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
        }

        [HttpPut("regras/{id:guid}")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> Atualizar(Guid id, [FromBody] SalvarAgendaDisponibilidadeRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            try
            {
                var response = await _service.AtualizarAsync(estabelecimentoId, id, request);
                return Ok(ApiResponse<AgendaDisponibilidadeDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (InvalidOperationException ex)
            {
                return ConflictErrorResponse(ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
        }

        [HttpPatch("regras/{id:guid}/status")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> AtualizarStatus(Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
            {
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");
            }

            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
            {
                return error!;
            }

            var updated = await _service.AtualizarStatusAsync(estabelecimentoId, id, request.Ativo);
            return updated
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
        }

        [HttpDelete("regras/{id:guid}")]
        [RequirePermission("Configuracoes", "deletar")]
        public async Task<IActionResult> Excluir(Guid id)
        {
            if (!TryResolveCurrentEstabelecimentoAndModule("Disponibilidade", out var estabelecimentoId, out var error))
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
