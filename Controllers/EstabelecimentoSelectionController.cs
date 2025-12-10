using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Dtos.Estabelecimentos;
using APIBack.Automation.Services.Interface;
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
        public async Task<IActionResult> GetMeEstabelecimentos()
        {
            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { success = false, error = "Usuário não autenticado." });
            }

            try
            {
                var estabelecimentos = await _selectionService.ListarEstabelecimentosAsync(userId.Value);
                return Ok(new { success = true, data = estabelecimentos });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao listar estabelecimentos do usuário {UserId}", userId.Value);
                return StatusCode(500, new { success = false, error = "Erro ao listar estabelecimentos." });
            }
        }

        [HttpPost("auth/definir-estabelecimento")]
        [Authorize]
        public async Task<IActionResult> DefinirEstabelecimento([FromBody] DefinirEstabelecimentoAtivoRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { success = false, error = "Requisição inválida." });
            }

            var userId = HttpContext.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized(new { success = false, error = "Usuário não autenticado." });
            }

            try
            {
                var response = await _selectionService.DefinirEstabelecimentoAtivoAsync(userId.Value, request.EstabelecimentoId);
                return Ok(new { success = true, data = response });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Erro ao definir estabelecimento {EstabelecimentoId} para usuário {UserId}",
                    request.EstabelecimentoId, userId.Value);
                return StatusCode(500, new { success = false, error = "Erro ao definir estabelecimento." });
            }
        }
    }
}
