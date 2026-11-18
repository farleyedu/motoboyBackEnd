using APIBack.Assistente;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Assistente da Zippy (zippy-admin/docs/assistente-ajuda/CONTRATO-API.md). Qualquer pessoa logada na loja conversa; ações só
/// quem teria a permissão na tela, conferida de novo no confirmar com o login de quem clicou.
/// </summary>
[ApiController]
[Route("api/v2/assistente")]
public sealed class AssistenteController(IAssistenteService service) : ControllerBase
{
    [HttpPost("mensagens")]
    public Task<IActionResult> Enviar([FromBody] EnviarMensagemRequest? req) =>
        Run(ctx => service.Enviar(ctx, req ?? new EnviarMensagemRequest(), HttpContext.RequestAborted));

    [HttpPost("acoes/{acaoId:guid}/confirmar")]
    public Task<IActionResult> Confirmar(Guid acaoId) => Run(ctx => service.Confirmar(ctx, acaoId, HttpContext.RequestAborted));

    [HttpPost("acoes/{acaoId:guid}/recusar")]
    public Task<IActionResult> Recusar(Guid acaoId) => Run(ctx => service.Recusar(ctx, acaoId, HttpContext.RequestAborted));

    /// <summary>Envia o rascunho de chamado do cartão, com a conversa junto (CONTRATO-API §9).</summary>
    [HttpPost("chamados")]
    public Task<IActionResult> Chamado([FromBody] EnviarChamadoRequest? req) =>
        Run(ctx => service.EnviarChamado(ctx, req ?? new EnviarChamadoRequest(), HttpContext.RequestAborted));

    [HttpGet("conversas/{conversaId:guid}")]
    public Task<IActionResult> Conversa(Guid conversaId) => Run(ctx => service.Conversa(ctx, conversaId, HttpContext.RequestAborted));

    private async Task<IActionResult> Run<T>(Func<ContextoAcao, Task<T>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetUserId() is not int usuario || usuario <= 0 || HttpContext.GetEstabelecimentoId() is not Guid loja || loja == Guid.Empty)
            return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        var superAdmin = HttpContext.IsSuperAdmin();
        var ctx = new ContextoAcao(loja, usuario, (m, a) => superAdmin || HttpContext.TemPermissao(m, a), HttpContext.GetEstabelecimentoModulosAtivos());
        try
        {
            return Ok(ApiResponse<T>.Ok(await acao(ctx)));
        }
        catch (AssistenteException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo));
        }
    }
}
