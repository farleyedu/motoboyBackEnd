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
    /// <summary>
    /// Numeros de teste da Zippy (varios; cada um atende uma loja por vez). So a Gestao. Erros: 400 corpo malformado, 403 sem permissao,
    /// 404 numero ou destino inexistente, 409 numero/Phone Number ID ja cadastrado, 422 regra de negocio.
    /// </summary>
    [ApiController]
    [Route("api/v2/atendimento/canais-teste")]
    public sealed class CanaisTesteController : ApiControllerBase
    {
        private readonly ICanalCentralService _teste;
        private readonly ILogger<CanaisTesteController> _logger;

        public CanaisTesteController(ICanalCentralService teste, ILogger<CanaisTesteController> logger)
        {
            _teste = teste;
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> Listar() => Executar(StatusCodes.Status200OK, ator => _teste.ListarTesteAsync(ator));

        [HttpPost]
        public Task<IActionResult> Criar([FromBody] CanalTesteRequest request) =>
            Executar(StatusCodes.Status201Created, ator => _teste.CriarTesteAsync(ator, request));

        [HttpPut("{id:guid}")]
        public Task<IActionResult> Alterar(Guid id, [FromBody] CanalTesteAtualizacao request) =>
            Executar(StatusCodes.Status200OK, ator => _teste.AtualizarTesteAsync(ator, id, request));

        [HttpPost("{id:guid}/verificar")]
        public Task<IActionResult> Verificar(Guid id) => Executar(StatusCodes.Status200OK, ator => _teste.VerificarTesteAsync(ator, id));

        private async Task<IActionResult> Executar<T>(int sucesso, Func<AtendimentoAtor, Task<T>> operacao)
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
                return StatusCode(sucesso, ApiResponse<T>.Ok(await operacao(ator)));
            }
            catch (ArgumentException ex)
            {
                return BadRequestErrorResponse(ex.Message);
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
            catch (CanalConflitoException ex)
            {
                return ConflictErrorResponse(ex.Message, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [ex.Campo] = new List<string> { ex.Message }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
