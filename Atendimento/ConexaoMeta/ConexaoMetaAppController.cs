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

namespace APIBack.Atendimento.ConexaoMeta
{
    /// <summary>App central ZippyGo do "Conectar com a Meta" (Gestao > Plataforma). So a Gestao; o App Secret nunca volta.</summary>
    [ApiController]
    [Route("api/v2/atendimento/conexao-meta/app")]
    public sealed class ConexaoMetaAppController : ApiControllerBase
    {
        private readonly IConexaoMetaService _conexao;
        private readonly ILogger<ConexaoMetaAppController> _logger;

        public ConexaoMetaAppController(IConexaoMetaService conexao, ILogger<ConexaoMetaAppController> logger)
        {
            _conexao = conexao;
            _logger = logger;
        }

        /// <summary>Cadastro atual + o que falta + verificacao na Meta (credenciais e campos do webhook do app).</summary>
        [HttpGet]
        public Task<IActionResult> Obter() => Executar(ator => _conexao.ObterAppAsync(ator));

        /// <summary>Grava o app. App Secret vazio mantem o atual. Confere na Meta antes de gravar.</summary>
        [HttpPut]
        public Task<IActionResult> Salvar([FromBody] SalvarAppMetaRequest request) => Executar(ator => _conexao.SalvarAppAsync(ator, request));

        private async Task<IActionResult> Executar<T>(Func<AtendimentoAtor, Task<T>> operacao)
        {
            var usuarioId = HttpContext.GetUserId();
            if (!usuarioId.HasValue) return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));

            var ator = new AtendimentoAtor(
                usuarioId.Value, HttpContext.IsSuperAdmin(), HttpContext.GetEstabelecimentoId(), (modulo, acao) => HttpContext.TemPermissao(modulo, acao));

            try
            {
                return Ok(ApiResponse<T>.Ok(await operacao(ator)));
            }
            catch (WhatsappException ex)
            {
                _logger.LogWarning("[atend.api] ev=erro_whatsapp rota={Rota} codigo={Codigo} campo={Campo}", Request.Path.Value, ex.Erro.Codigo, ex.Erro.Campo);
                return StatusCode(ex.Http, ApiResponse<object>.Fail(ex.Erro.Mensagem, ex.Erro.Codigo, ex.Erro.Detalhes));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
