using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.AdminUsers;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/admin/usuarios")]
    public class AdminUsuariosController : ControllerBase
    {
        private readonly IAdminUsuariosService _service;
        private readonly ILogger<AdminUsuariosController> _logger;

        public AdminUsuariosController(
            IAdminUsuariosService service,
            ILogger<AdminUsuariosController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet("contexto")]
        [RequirePermission("Usuarios", "visualizar")]
        [ProducesResponseType(typeof(ApiResponse<AdminUsuariosContextoResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> ObterContexto()
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("UsuÃ¡rio nÃ£o autenticado."));
            }

            try
            {
                var response = await _service.ObterContextoAsync(
                    userId.Value,
                    HttpContext.GetEstabelecimentoId(),
                    HttpContext.IsSuperAdmin());
                return Ok(ApiResponse<AdminUsuariosContextoResponse>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao montar contexto da tela de usuarios para UserId={UserId}", userId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao carregar contexto de usuarios."));
            }
        }

        [HttpGet]
        [RequirePermission("Usuarios", "visualizar")]
        [ProducesResponseType(typeof(ApiResponse<AdminUsuariosListResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> Listar()
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("UsuÃ¡rio nÃ£o autenticado."));
            }

            try
            {
                var response = await _service.ListarUsuariosAsync(
                    userId.Value,
                    HttpContext.GetEstabelecimentoId(),
                    HttpContext.IsSuperAdmin());
                return Ok(ApiResponse<AdminUsuariosListResponse>.Ok(response));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar usuarios administrativos para UserId={UserId}", userId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao listar usuarios."));
            }
        }

        [HttpPost]
        [RequirePermission("Usuarios", "criar")]
        [ProducesResponseType(typeof(ApiResponse<AdminUsuarioListItemDto>), StatusCodes.Status201Created)]
        public async Task<IActionResult> Criar([FromBody] CreateAdminUsuarioRequest request)
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("UsuÃ¡rio nÃ£o autenticado."));
            }

            try
            {
                var response = await _service.CriarUsuarioAsync(
                    userId.Value,
                    HttpContext.GetEmpresaId(),
                    HttpContext.GetEstabelecimentoId(),
                    HttpContext.IsSuperAdmin(),
                    request);

                return StatusCode(StatusCodes.Status201Created, ApiResponse<AdminUsuarioListItemDto>.Ok(response));
            }
            catch (AdminUsuarioValidationException ex)
            {
                return UnprocessableEntity(new
                {
                    success = false,
                    error = ex.Message,
                    errors = ex.Errors
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao criar usuario administrativo para UserId={UserId}", userId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao criar usuario."));
            }
        }
    }
}
