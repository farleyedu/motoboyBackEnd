using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Vinculos;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Curadoria Zippy com fila e decisões (tela /apps/curadoria). Só super admin (Curadoria:visualizar não existe para loja).
/// As rotas antigas de candidatos (api/gestao/curadoria) continuam em GestaoVinculosController.
/// </summary>
[ApiController]
[Route("api/curadoria")]
public sealed class CuradoriaController(ICuradoriaDecisoesService service) : ControllerBase
{
    [HttpGet("casos")]
    public Task<IActionResult> Casos(CancellationToken ct) => Run(async _ => await service.Casos(ct));

    [HttpGet("decisoes")]
    public Task<IActionResult> Decisoes(CancellationToken ct) => Run(async _ => await service.Decisoes(ct));

    [HttpPost("decisoes")]
    public Task<IActionResult> Decidir([FromBody] DecidirCuradoriaRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.Decidir(usuario, HttpContext.GetUserNome(), req ?? Vazio(), ct));

    [HttpPost("decisoes/lote")]
    public Task<IActionResult> Lote([FromBody] DecidirEmLoteRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.DecidirEmLote(usuario, HttpContext.GetUserNome(),
            req ?? new DecidirEmLoteRequest(null, null, null, null), ct));

    [HttpPost("decisoes/{id:guid}/reabertura")]
    public Task<IActionResult> Reabrir(Guid id, [FromBody] ReabrirDecisaoRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.Reabrir(usuario, HttpContext.GetUserNome(), id, req ?? new ReabrirDecisaoRequest(null, null), ct));

    [HttpGet("produtos-globais")]
    public Task<IActionResult> ProdutosGlobais([FromQuery] string? busca, CancellationToken ct) =>
        Run(async _ => await service.ProdutosGlobais(busca, ct));

    private static DecidirCuradoriaRequest Vazio() => new(null, null, null, null, null);

    private async Task<IActionResult> Run(Func<int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetUserId() is not int usuario) return Unauthorized();
        try
        {
            if (!HttpContext.IsSuperAdmin()) throw new GestaoException("SEM_PERMISSAO", "A curadoria é só da equipe Zippy.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
