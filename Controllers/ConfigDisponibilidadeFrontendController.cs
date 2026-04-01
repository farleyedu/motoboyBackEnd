using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Agendamentos;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/configuracoes/{estabelecimentoId:guid}/disponibilidade")]
    public class ConfigDisponibilidadeFrontendController : EstabelecimentoScopedControllerBase
    {
        private readonly IAgendaDisponibilidadeService _disponibilidadeService;
        private readonly IEstabelecimentoAgendamentoConfigService _configService;

        public ConfigDisponibilidadeFrontendController(
            IAgendaDisponibilidadeService disponibilidadeService,
            IEstabelecimentoAgendamentoConfigService configService)
        {
            _disponibilidadeService = disponibilidadeService;
            _configService = configService;
        }

        // GET /api/configuracoes/{estabelecimentoId}/disponibilidade
        // Retorna snapshot completo: configuracao + regras + profissionais
        [HttpGet]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> ObterSnapshot(Guid estabelecimentoId)
        {
            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var configTask = _configService.ObterAsync(effectiveId);
            var regrasTask = _disponibilidadeService.ListarAsync(effectiveId, null, null, null, null, 1, 500);
            var profissionaisTask = _disponibilidadeService.ListarProfissionaisAsync(effectiveId);

            await Task.WhenAll(configTask, regrasTask, profissionaisTask);

            var snapshot = new ConfigDisponibilidadeSnapshotDto
            {
                Configuracao = configTask.Result,
                Regras = regrasTask.Result.Itens.ToList(),
                Profissionais = profissionaisTask.Result.Itens.ToList()
            };

            return Ok(ApiResponse<ConfigDisponibilidadeSnapshotDto>.Ok(snapshot));
        }

        // PUT /api/configuracoes/{estabelecimentoId}/disponibilidade/configuracao
        [HttpPut("configuracao")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> SalvarConfiguracao(Guid estabelecimentoId, [FromBody] SalvarEstabelecimentoAgendamentoConfigRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            try
            {
                var result = await _configService.SalvarAsync(effectiveId, request);
                return Ok(ApiResponse<EstabelecimentoAgendamentoConfigDto>.Ok(result));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
        }

        // GET /api/configuracoes/{estabelecimentoId}/disponibilidade/regras
        [HttpGet("regras")]
        [RequirePermission("Configuracoes", "visualizar")]
        public async Task<IActionResult> ListarRegras(Guid estabelecimentoId)
        {
            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var paged = await _disponibilidadeService.ListarAsync(effectiveId, null, null, null, null, 1, 500);
            return Ok(ApiResponse<IReadOnlyCollection<AgendaDisponibilidadeDto>>.Ok(paged.Itens));
        }

        // POST /api/configuracoes/{estabelecimentoId}/disponibilidade/regras
        [HttpPost("regras")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> CriarRegra(Guid estabelecimentoId, [FromBody] SalvarAgendaDisponibilidadeRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            try
            {
                var result = await _disponibilidadeService.CriarAsync(effectiveId, request);
                return Ok(ApiResponse<AgendaDisponibilidadeDto>.Ok(result));
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

        // PUT /api/configuracoes/{estabelecimentoId}/disponibilidade/regras/{id}
        [HttpPut("regras/{id:guid}")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> AtualizarRegra(Guid estabelecimentoId, Guid id, [FromBody] SalvarAgendaDisponibilidadeRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            try
            {
                var result = await _disponibilidadeService.AtualizarAsync(effectiveId, id, request);
                return Ok(ApiResponse<AgendaDisponibilidadeDto>.Ok(result));
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

        // DELETE /api/configuracoes/{estabelecimentoId}/disponibilidade/regras/{id}
        [HttpDelete("regras/{id:guid}")]
        [RequirePermission("Configuracoes", "deletar")]
        public async Task<IActionResult> ExcluirRegra(Guid estabelecimentoId, Guid id)
        {
            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var removed = await _disponibilidadeService.ExcluirAsync(effectiveId, id);
            return removed
                ? Ok(ApiResponse<object>.Ok(new { }))
                : NotFoundErrorResponse("Registro nao encontrado.");
        }

        // PATCH /api/configuracoes/{estabelecimentoId}/disponibilidade/regras/{id}/ativo
        [HttpPatch("regras/{id:guid}/ativo")]
        [RequirePermission("Configuracoes", "editar")]
        public async Task<IActionResult> AtualizarAtivoRegra(Guid estabelecimentoId, Guid id, [FromBody] AtualizarStatusRequest? request)
        {
            if (request == null)
                return BadRequestErrorResponse("Corpo da requisicao e obrigatorio.");

            if (!TryResolveAndValidate("Disponibilidade", estabelecimentoId, out var effectiveId, out var error))
                return error!;

            var updated = await _disponibilidadeService.AtualizarStatusAsync(effectiveId, id, request.Ativo);
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
