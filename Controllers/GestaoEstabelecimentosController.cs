using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.DTOs.Gestao;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/gestao/estabelecimentos")]
    public class GestaoEstabelecimentosController : GestaoControllerBase
    {
        private readonly IGestaoService _service;
        private readonly ILogger<GestaoEstabelecimentosController> _logger;

        public GestaoEstabelecimentosController(IGestaoService service, ILogger<GestaoEstabelecimentosController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<GestaoEstabelecimentoDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Listar()
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Estabelecimentos", "visualizar"), ("Configuracoes", "visualizar"), ("Usuarios", "visualizar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.ListarEstabelecimentosAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin);
                return Ok(ApiResponse<IReadOnlyCollection<GestaoEstabelecimentoDto>>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar estabelecimentos de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao listar estabelecimentos."));
            }
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<GestaoEstabelecimentoDto>), StatusCodes.Status201Created)]
        public async Task<IActionResult> Criar([FromBody] SalvarEstabelecimentoRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Estabelecimentos", "criar"), ("Configuracoes", "criar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.CriarEstabelecimentoAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    request);
                return StatusCode(StatusCodes.Status201Created, ApiResponse<GestaoEstabelecimentoDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                _logger.LogWarning(
                    "Validacao ao criar estabelecimento de gestao falhou para UserId={UserId}. NomeFantasia={NomeFantasia} EmpresaId={EmpresaId} TipoEstabelecimentoId={TipoEstabelecimentoId} TipoEstabelecimentoSlug={TipoEstabelecimentoSlug} Errors={@Errors}",
                    CurrentUserId.Value,
                    request.NomeFantasia,
                    request.EmpresaId,
                    request.TipoEstabelecimentoId,
                    request.TipoEstabelecimentoSlug,
                    ex.Errors);
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar estabelecimento de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao criar estabelecimento."));
            }
        }

        [HttpPut("{estabelecimentoId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<GestaoEstabelecimentoDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Atualizar(Guid estabelecimentoId, [FromBody] SalvarEstabelecimentoRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Estabelecimentos", "editar"), ("Configuracoes", "editar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.AtualizarEstabelecimentoAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    estabelecimentoId,
                    request);
                return Ok(ApiResponse<GestaoEstabelecimentoDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                _logger.LogWarning(
                    "Validacao ao atualizar estabelecimento de gestao falhou para UserId={UserId} EstabelecimentoId={EstabelecimentoId}. NomeFantasia={NomeFantasia} EmpresaId={EmpresaId} TipoEstabelecimentoId={TipoEstabelecimentoId} TipoEstabelecimentoSlug={TipoEstabelecimentoSlug} Errors={@Errors}",
                    CurrentUserId.Value,
                    estabelecimentoId,
                    request.NomeFantasia,
                    request.EmpresaId,
                    request.TipoEstabelecimentoId,
                    request.TipoEstabelecimentoSlug,
                    ex.Errors);
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar estabelecimento {EstabelecimentoId} para UserId={UserId}", estabelecimentoId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar estabelecimento."));
            }
        }

        [HttpPatch("{estabelecimentoId:guid}/status")]
        public async Task<IActionResult> AtualizarStatus(Guid estabelecimentoId, [FromBody] AtualizarStatusEstabelecimentoRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Estabelecimentos", "desativar"), ("Estabelecimentos", "editar"), ("Configuracoes", "editar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                await _service.AtualizarStatusEstabelecimentoAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    estabelecimentoId,
                    request.Ativa);
                return Ok(ApiResponse<object>.Ok(new { estabelecimentoId, ativa = request.Ativa }));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar status do estabelecimento {EstabelecimentoId} para UserId={UserId}", estabelecimentoId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar status do estabelecimento."));
            }
        }
    }
}
