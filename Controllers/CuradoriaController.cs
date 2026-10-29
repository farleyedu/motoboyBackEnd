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
public sealed class CuradoriaController(ICuradoriaDecisoesService service, ICuradoriaGlobalService global) : ControllerBase
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

    // Correção do catálogo global (L7): cada uma vira decisão "corrigir" com caso global:<alvo>:<id>.

    [HttpGet("global/produtos")]
    public Task<IActionResult> GlobalProdutos([FromQuery] string? busca, CancellationToken ct) =>
        Run(async _ => await global.Produtos(busca, ct));

    [HttpPost("global/produtos/{id:guid}/correcao")]
    public Task<IActionResult> CorrigirProduto(Guid id, [FromBody] CorrigirProdutoGlobalRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.CorrigirProduto(usuario, HttpContext.GetUserNome(), id,
            req ?? new CorrigirProdutoGlobalRequest(null, null, null, null, null), ct));

    [HttpGet("global/kits")]
    public Task<IActionResult> Kits(CancellationToken ct) => Run(async _ => await global.Kits(ct));

    [HttpPost("global/kits")]
    public Task<IActionResult> CriarKit([FromBody] SalvarKitRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.CriarKit(usuario, HttpContext.GetUserNome(), req ?? KitVazio, ct));

    [HttpPost("global/kits/{id:guid}/correcao")]
    public Task<IActionResult> CorrigirKit(Guid id, [FromBody] SalvarKitRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.CorrigirKit(usuario, HttpContext.GetUserNome(), id, req ?? KitVazio, ct));

    [HttpGet("global/categorias")]
    public Task<IActionResult> Categorias(CancellationToken ct) => Run(async _ => await global.Categorias(ct));

    [HttpPost("global/categorias")]
    public Task<IActionResult> CriarCategoria([FromBody] SalvarCategoriaRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.CriarCategoria(usuario, HttpContext.GetUserNome(), req ?? CategoriaVazia, ct));

    [HttpPost("global/categorias/{id:guid}/correcao")]
    public Task<IActionResult> CorrigirCategoria(Guid id, [FromBody] SalvarCategoriaRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.CorrigirCategoria(usuario, HttpContext.GetUserNome(), id, req ?? CategoriaVazia, ct));

    [HttpGet("global/historico")]
    public Task<IActionResult> HistoricoGlobal([FromQuery] string? casoId, CancellationToken ct) =>
        Run(async _ => await global.Historico(casoId, ct));

    [HttpGet("criterios")]
    public Task<IActionResult> Criterios(CancellationToken ct) => Run(async _ => await global.Criterios(ct));

    [HttpPost("criterios")]
    public Task<IActionResult> SalvarCriterios([FromBody] SalvarCriteriosRequest? req, CancellationToken ct) =>
        Run(async usuario => await global.SalvarCriterios(usuario, HttpContext.GetUserNome(), req ?? new SalvarCriteriosRequest(null, null, null, null), ct));

    private static readonly SalvarKitRequest KitVazio = new(null, null, null, null, null);
    private static readonly SalvarCategoriaRequest CategoriaVazia = new(null, null, null);

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
