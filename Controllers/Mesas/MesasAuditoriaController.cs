using APIBack.Mesas.Auditoria;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers.Mesas;

/// <summary>Auditoria do salão (CONTRATO-API §4.1): só dono e gerente (<c>Mesas.gerir</c>). Livro imutável, 50 por página.</summary>
[Route("api/v2/mesas/auditoria")]
public sealed class MesasAuditoriaController(IMesasNucleoService nucleo, IAuditoriaService auditoria) : MesasControllerBase(nucleo)
{
    /// <summary><c>?quem=</c> (id do usuário ou parte do nome) <c>&amp;acao=a,b&amp;de=&amp;ate=&amp;mesaId=&amp;ocorrencias=true&amp;cursor=&amp;limite=</c>.</summary>
    [HttpGet]
    public Task<IActionResult> Listar([FromQuery] string? quem, [FromQuery] string? acao, [FromQuery] DateTime? de, [FromQuery] DateTime? ate,
        [FromQuery] Guid? mesaId, [FromQuery] bool? ocorrencias, [FromQuery] string? cursor, [FromQuery] int? limite) =>
        Run(MesasPermissoes.Gerir, async ctx => await auditoria.ListarAsync(ctx.Loja,
            new FiltroAuditoria(quem, acao, de, ate, mesaId, ocorrencias, cursor, limite), HttpContext.RequestAborted), gestorPassaDesligado: true);

    [HttpGet("resumo")]
    public Task<IActionResult> Resumo() =>
        Run(MesasPermissoes.Gerir, async ctx => await auditoria.ResumoAsync(ctx.Loja, HttpContext.RequestAborted), gestorPassaDesligado: true);
}
