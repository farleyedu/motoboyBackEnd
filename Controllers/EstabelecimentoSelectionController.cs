using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos.Estabelecimentos;
using APIBack.Automation.Services.Interface;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api")]
    public class EstabelecimentoSelectionController : ControllerBase
    {
        private readonly IEstabelecimentoSelectionService _selectionService;
        private readonly ILogger<EstabelecimentoSelectionController> _logger;

        public EstabelecimentoSelectionController(
            IEstabelecimentoSelectionService selectionService,
            ILogger<EstabelecimentoSelectionController> logger)
        {
            _selectionService = selectionService;
            _logger = logger;
        }

        [HttpGet("me/estabelecimentos")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<UsuarioEstabelecimentoDto>>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetMeEstabelecimentos()
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));
            }

            try
            {
                var estabelecimentos = await _selectionService.ListarEstabelecimentosAsync(userId.Value);
                return Ok(ApiResponse<IReadOnlyCollection<UsuarioEstabelecimentoDto>>.Ok(estabelecimentos));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar estabelecimentos do usuário {UserId}", userId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao listar estabelecimentos."));
            }
        }

        [HttpPost("auth/definir-estabelecimento")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<DefinirEstabelecimentoAtivoResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DefinirEstabelecimento([FromBody] DefinirEstabelecimentoAtivoRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Fail("Requisição inválida."));
            }

            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuário não autenticado."));
            }

            try
            {
                var response = await _selectionService.DefinirEstabelecimentoAtivoAsync(userId.Value, request.EstabelecimentoId);
                return Ok(ApiResponse<DefinirEstabelecimentoAtivoResponse>.Ok(response));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
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
                _logger.LogError(
                    ex,
                    "Erro ao definir estabelecimento {EstabelecimentoId} para usuário {UserId}",
                    request.EstabelecimentoId,
                    userId.Value);
                return StatusCode(500, ApiResponse<object>.Fail("Erro ao definir estabelecimento."));
            }
        }
    }
}
