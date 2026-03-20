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
    [Route("api/gestao/empresas")]
    public class GestaoEmpresasController : GestaoControllerBase
    {
        private readonly IGestaoService _service;
        private readonly ILogger<GestaoEmpresasController> _logger;

        public GestaoEmpresasController(IGestaoService service, ILogger<GestaoEmpresasController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<GestaoEmpresaDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Listar()
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Empresas", "visualizar"), ("Configuracoes", "visualizar"), ("Usuarios", "visualizar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.ListarEmpresasAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin);
                return Ok(ApiResponse<IReadOnlyCollection<GestaoEmpresaDto>>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar empresas de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao listar empresas."));
            }
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<GestaoEmpresaDto>), StatusCodes.Status201Created)]
        public async Task<IActionResult> Criar([FromBody] SalvarEmpresaRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Empresas", "criar"), ("Configuracoes", "criar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.CriarEmpresaAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    request);
                return StatusCode(StatusCodes.Status201Created, ApiResponse<GestaoEmpresaDto>.Ok(response));
            }
            catch (RequestValidationException ex)
            {
                _logger.LogWarning(
                    "Validacao ao criar empresa de gestao falhou para UserId={UserId}. NomeFantasia={NomeFantasia} Errors={@Errors}",
                    CurrentUserId.Value,
                    request.NomeFantasia,
                    ex.Errors);
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar empresa de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao criar empresa."));
            }
        }

        [HttpPut("{empresaId:guid}")]
        [ProducesResponseType(typeof(ApiResponse<GestaoEmpresaDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Atualizar(Guid empresaId, [FromBody] SalvarEmpresaRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Empresas", "editar"), ("Configuracoes", "editar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.AtualizarEmpresaAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    empresaId,
                    request);
                return Ok(ApiResponse<GestaoEmpresaDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (RequestValidationException ex)
            {
                _logger.LogWarning(
                    "Validacao ao atualizar empresa de gestao falhou para UserId={UserId} EmpresaId={EmpresaId}. NomeFantasia={NomeFantasia} Errors={@Errors}",
                    CurrentUserId.Value,
                    empresaId,
                    request.NomeFantasia,
                    ex.Errors);
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar empresa {EmpresaId} para UserId={UserId}", empresaId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar empresa."));
            }
        }

        [HttpPatch("{empresaId:guid}/status")]
        public async Task<IActionResult> AtualizarStatus(Guid empresaId, [FromBody] AtualizarStatusEmpresaRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Empresas", "desativar"), ("Empresas", "editar"), ("Configuracoes", "editar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                await _service.AtualizarStatusEmpresaAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    empresaId,
                    request.Ativa);
                return Ok(ApiResponse<object>.Ok(new { empresaId, ativa = request.Ativa }));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar status da empresa {EmpresaId} para UserId={UserId}", empresaId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar status da empresa."));
            }
        }
    }
}
