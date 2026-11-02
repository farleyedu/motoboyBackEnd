using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.GestaoMensagens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers
{
    /// <summary>
    /// Mensagens da Gestao para lojas e leads (so a equipe Zippy: super admin). Erros: 401 sem login, 403 sem permissao (inclusive no numero),
    /// 404 destino/contato/numero inexistente, 409 contato ambiguo, numero desconectado, chave reutilizada, 422 regra (janela fechada sem modelo,
    /// modelo de outro numero, variaveis, consentimento). Toda falha traz codigo estavel e o proximo passo em details.proximoPasso.
    /// </summary>
    [ApiController]
    public sealed class GestaoMensagensController : GestaoControllerBase
    {
        private readonly IGestaoMensagensService _service;

        public GestaoMensagensController(IGestaoMensagensService service) => _service = service;

        [HttpGet("api/gestao/canais")]
        public Task<IActionResult> Canais() => Executar((ator, ct) => _service.CanaisAsync(ator, ct));

        [HttpGet("api/gestao/mensagens/opcoes")]
        public Task<IActionResult> Opcoes([FromQuery] Guid? lojaId, [FromQuery] Guid? oportunidadeId, [FromQuery] string? contatoId) =>
            Executar((ator, ct) => _service.OpcoesAsync(ator, lojaId, oportunidadeId, contatoId, ct));

        [HttpGet("api/gestao/mensagens")]
        public Task<IActionResult> Historico([FromQuery] Guid? lojaId, [FromQuery] Guid? oportunidadeId) =>
            Executar((ator, ct) => _service.HistoricoAsync(ator, lojaId, oportunidadeId, ct));

        [HttpPost("api/gestao/mensagens")]
        public Task<IActionResult> Enviar([FromBody] EnviarMensagemRequest? request) =>
            Executar((ator, ct) => _service.EnviarAsync(ator, request, ct));

        /// <summary>WhatsApp pessoal sem Meta: quem enviou por fora confirma. Vira "enviado por fora", sem recibo.</summary>
        [HttpPost("api/gestao/mensagens/{id:guid}/confirmar-envio-externo")]
        public Task<IActionResult> ConfirmarExterno(Guid id) => Executar((ator, ct) => _service.ConfirmarEnvioExternoAsync(ator, id, ct));

        private async Task<IActionResult> Executar<T>(Func<AtorGestao, CancellationToken, Task<T>> operacao)
        {
            if (!CurrentUserId.HasValue) return UnauthorizedResponse();
            if (!CurrentIsSuperAdmin) return ForbiddenResponse();
            Response.Headers.CacheControl = "no-store";
            try
            {
                var ator = new AtorGestao(CurrentUserId.Value, null, true);
                return Ok(ApiResponse<T>.Ok(await operacao(ator, HttpContext.RequestAborted)));
            }
            catch (GestaoMensagensException ex)
            {
                return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, new { proximoPasso = ex.ProximoPasso, dados = ex.Detalhes }));
            }
        }
    }
}
