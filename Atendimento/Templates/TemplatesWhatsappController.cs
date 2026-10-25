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

namespace APIBack.Atendimento.Templates
{
    /// <summary>
    /// Modelos oficiais de mensagem (templates da Meta) de um numero da loja. Quem pode o que e decidido no servico: quem le a loja
    /// lista e consulta; criar, sincronizar e excluir exige WhatsApp/configurar na propria loja ou a Gestao.
    /// </summary>
    [ApiController]
    [Route("api/v2/atendimento/estabelecimentos/{estabelecimentoId:guid}/canais/{canalId:guid}/templates")]
    public sealed class TemplatesWhatsappController : ApiControllerBase
    {
        private readonly ITemplatesWhatsappService _templates;
        private readonly ILogger<TemplatesWhatsappController> _logger;

        public TemplatesWhatsappController(ITemplatesWhatsappService templates, ILogger<TemplatesWhatsappController> logger)
        {
            _templates = templates;
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> Listar(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _templates.ListarAsync(ator, estabelecimentoId, canalId));

        /// <summary>Confere token, WABA e permissao whatsapp_business_management, sem gravar nada.</summary>
        [HttpGet("diagnostico")]
        public Task<IActionResult> Diagnosticar(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _templates.DiagnosticarAsync(ator, estabelecimentoId, canalId));

        [HttpGet("{templateId:guid}")]
        public Task<IActionResult> Obter(Guid estabelecimentoId, Guid canalId, Guid templateId) =>
            Executar(ator => _templates.ObterAsync(ator, estabelecimentoId, canalId, templateId));

        /// <summary>Valida o modelo pelas regras da Meta sem enviar (mesmas regras do POST).</summary>
        [HttpPost("validar")]
        public Task<IActionResult> Validar(Guid estabelecimentoId, Guid canalId, [FromBody] CriarTemplateRequest? request) =>
            Executar(ator =>
            {
                ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
                return Task.FromResult(_templates.Validar(request));
            });

        /// <summary>201 quando a Meta confirmou; 202 quando a Meta nao respondeu e o modelo ficou "verificando".</summary>
        [HttpPost]
        public Task<IActionResult> Criar(Guid estabelecimentoId, Guid canalId, [FromBody] CriarTemplateRequest? request) =>
            Executar(ator => _templates.CriarAsync(ator, estabelecimentoId, canalId, request),
                r => r.Confirmado ? StatusCodes.Status201Created : StatusCodes.Status202Accepted);

        [HttpPost("sincronizar")]
        public Task<IActionResult> Sincronizar(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _templates.SincronizarAsync(ator, estabelecimentoId, canalId));

        [HttpPost("{templateId:guid}/sincronizar")]
        public Task<IActionResult> SincronizarUm(Guid estabelecimentoId, Guid canalId, Guid templateId) =>
            Executar(ator => _templates.SincronizarUmAsync(ator, estabelecimentoId, canalId, templateId));

        /// <summary>Exclui so este modelo na Meta. Confirmacao obrigatoria: ?confirmarNome=nome_tecnico.</summary>
        [HttpDelete("{templateId:guid}")]
        public Task<IActionResult> Excluir(Guid estabelecimentoId, Guid canalId, Guid templateId, [FromQuery] string? confirmarNome) =>
            Executar(ator => _templates.ExcluirAsync(ator, estabelecimentoId, canalId, templateId, confirmarNome));

        private async Task<IActionResult> Executar<T>(Func<AtendimentoAtor, Task<T>> operacao, Func<T, int>? sucesso = null)
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
                var resultado = await operacao(ator);
                return StatusCode(sucesso?.Invoke(resultado) ?? StatusCodes.Status200OK, ApiResponse<T>.Ok(resultado));
            }
            catch (WhatsappException ex)
            {
                // Codigo estavel + como resolver + campo; nunca token, app secret ou o corpo cru da Meta.
                _logger.LogWarning("[wa.tpl.api] ev=erro_whatsapp rota={Rota} codigo={Codigo} http={Http}", Request.Path.Value, ex.Erro.Codigo, ex.Http);
                return StatusCode(ex.Http, ApiResponse<object>.Fail(ex.Erro.Mensagem, ex.Erro.Codigo, ex.Erro.Detalhes));
            }
            catch (TemplateRequisicaoInvalidaException ex)
            {
                return BadRequestErrorResponse(ex.Message, ex.Errors);
            }
            catch (RequestValidationException ex)
            {
                // Inclui TemplateIncompativelException: conteudo que a Meta recusaria (422, todos os campos de uma vez).
                return ValidationErrorResponse(ex);
            }
            catch (TemplateConflitoException ex)
            {
                return ConflictErrorResponse(ex.Message);
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
                _logger.LogError(ex, "[wa.tpl.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
