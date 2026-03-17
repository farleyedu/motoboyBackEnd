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
    [Route("api/gestao/usuarios")]
    public class GestaoUsuariosController : GestaoControllerBase
    {
        private readonly IGestaoService _service;
        private readonly ILogger<GestaoUsuariosController> _logger;

        public GestaoUsuariosController(IGestaoService service, ILogger<GestaoUsuariosController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet]
        [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<GestaoUsuarioDto>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Listar()
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Usuarios", "visualizar"), ("Configuracoes", "visualizar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.ListarUsuariosAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin);
                return Ok(ApiResponse<IReadOnlyCollection<GestaoUsuarioDto>>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar usuarios de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao listar usuarios."));
            }
        }

        [HttpPost]
        [ProducesResponseType(typeof(ApiResponse<GestaoUsuarioDto>), StatusCodes.Status201Created)]
        public async Task<IActionResult> Criar([FromBody] SalvarUsuarioRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Usuarios", "criar"), ("Configuracoes", "criar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.CriarUsuarioAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    request);
                return StatusCode(StatusCodes.Status201Created, ApiResponse<GestaoUsuarioDto>.Ok(response));
            }
            catch (AdminUsuarioValidationException ex)
            {
                return UnprocessableEntity(new { success = false, error = ex.Message, errors = ex.Errors });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar usuario de gestao para UserId={UserId}", CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao criar usuario."));
            }
        }

        [HttpPut("{userId:int}")]
        [ProducesResponseType(typeof(ApiResponse<GestaoUsuarioDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Atualizar(int userId, [FromBody] SalvarUsuarioRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Usuarios", "editar"), ("Configuracoes", "editar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                var response = await _service.AtualizarUsuarioAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    userId,
                    request);
                return Ok(ApiResponse<GestaoUsuarioDto>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (AdminUsuarioValidationException ex)
            {
                return UnprocessableEntity(new { success = false, error = ex.Message, errors = ex.Errors });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar usuario {TargetUserId} para UserId={UserId}", userId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar usuario."));
            }
        }

        [HttpPatch("{userId:int}/status")]
        public async Task<IActionResult> AtualizarStatus(int userId, [FromBody] AtualizarStatusUsuarioRequest request)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Usuarios", "editar"), ("Usuarios", "desativar"), ("Usuarios", "deletar"), ("Configuracoes", "editar"), ("Configuracoes", "deletar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                await _service.AtualizarStatusUsuarioAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    userId,
                    request.Status);
                return Ok(ApiResponse<object>.Ok(new { userId, status = request.Status }));
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
                _logger.LogError(ex, "Erro ao atualizar status do usuario {TargetUserId} para UserId={UserId}", userId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao atualizar status do usuario."));
            }
        }

        [HttpDelete("{userId:int}")]
        public async Task<IActionResult> Remover(int userId)
        {
            if (!CurrentUserId.HasValue)
            {
                return UnauthorizedResponse();
            }

            if (!HasAnyPermission(("Usuarios", "deletar"), ("Usuarios", "excluir"), ("Configuracoes", "deletar")))
            {
                return ForbiddenResponse();
            }

            try
            {
                await _service.RemoverUsuarioAsync(
                    CurrentUserId.Value,
                    CurrentEmpresaId,
                    CurrentEstabelecimentoId,
                    CurrentCompanyRole,
                    CurrentEstablishmentRole,
                    CurrentIsSuperAdmin,
                    userId);
                return Ok(ApiResponse<object>.Ok(new { userId }));
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
                _logger.LogError(ex, "Erro ao remover usuario {TargetUserId} para UserId={UserId}", userId, CurrentUserId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao remover usuario."));
            }
        }
    }
}
