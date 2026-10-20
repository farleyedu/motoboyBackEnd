using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <summary>Numero central de testes (um so para todas as lojas de teste). Quem pode o que e decidido no servico: so a Gestao.</summary>
    [ApiController]
    [Route("api/v2/atendimento/canal-central")]
    public sealed class CanalCentralController : ApiControllerBase
    {
        private readonly ICanalCentralService _central;
        private readonly ILogger<CanalCentralController> _logger;

        public CanalCentralController(ICanalCentralService central, ILogger<CanalCentralController> logger)
        {
            _central = central;
            _logger = logger;
        }

        /// <summary>Devolve o numero central, ou <c>data: null</c> se ainda nao foi cadastrado.</summary>
        [HttpGet]
        public Task<IActionResult> Obter() => Executar(ator => _central.ObterAsync(ator));

        [HttpPut]
        public Task<IActionResult> Salvar([FromBody] CanalCentralRequest request) =>
            Executar(ator => _central.SalvarAsync(ator, request));

        private async Task<IActionResult> Executar<T>(Func<AtendimentoAtor, Task<T>> operacao)
        {
            var usuarioId = HttpContext.GetUserId();
            if (!usuarioId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            var ator = new AtendimentoAtor(
                usuarioId.Value, HttpContext.IsSuperAdmin(), HttpContext.GetEstabelecimentoId(), (modulo, acao) => HttpContext.TemPermissao(modulo, acao));

            try
            {
                return Ok(ApiResponse<T>.Ok(await operacao(ator)));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
