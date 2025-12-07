using System;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Auth;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Requisi\u00e7\u00e3o inv\u00e1lida."
                });
            }

            try
            {
                var response = await _authService.LoginAsync(request);
                return Ok(new { success = true, data = response });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao processar login." });
            }
        }

        [HttpGet("estabelecimentos")]
        [Authorize]
        public async Task<IActionResult> ListarEstabelecimentos()
        {
            try
            {
                var userId = HttpContext.GetUserId();

                if (!userId.HasValue)
                {
                    return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
                }

                var estabelecimentos = await _authService.ListarEstabelecimentosDisponiveisAsync(userId.Value);
                return Ok(new { success = true, data = estabelecimentos });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao listar estabelecimentos." });
            }
        }

        [HttpPost("selecionar-estabelecimento")]
        [Authorize]
        public async Task<IActionResult> SelecionarEstabelecimento([FromBody] SelecionarEstabelecimentoRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Requisi\u00e7\u00e3o inv\u00e1lida."
                });
            }

            try
            {
                var userId = HttpContext.GetUserId();

                if (!userId.HasValue)
                {
                    return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
                }

                var response = await _authService.SelecionarEstabelecimentoAsync(userId.Value, request.EstabelecimentoId);
                return Ok(new { success = true, data = response });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { success = false, error = ex.Message });
            }
            catch
            {
                return StatusCode(500, new { success = false, error = "Erro ao selecionar estabelecimento." });
            }
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult ObterUsuarioAtual()
        {
            var payload = HttpContext.GetJwtPayload();

            if (payload == null || payload.UserId == null)
            {
                return Unauthorized(new { success = false, error = "Usu\u00e1rio n\u00e3o autenticado." });
            }

            var data = new
            {
                Id = payload.UserId,
                Nome = payload.Nome,
                Email = payload.Email,
                IsSuperAdmin = payload.IsSuperAdmin,
                EstabelecimentoAtual = payload.EstabelecimentoId.HasValue
                    ? new
                    {
                        Id = payload.EstabelecimentoId.Value,
                        Nome = payload.EstabelecimentoNome,
                        Tipo = payload.TipoEstabelecimento,
                        TipoAcesso = payload.TipoAcesso
                    }
                    : null,
                Permissoes = payload.Permissoes
            };

            return Ok(new { success = true, data });
        }
    }
}

