using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Catalogo;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Catálogo da loja e global (CONTRATOS, seção "Integração 6B — Catálogo"): produtos da loja, catálogo Zippy, kits, importar,
/// criar, finalidades, de-para de compras ("não controlar", "sugerir ao Zippy", vincular) e a busca da conferência.
/// Só o gerente (o operador de caixa não vê o Catálogo); escrever exige poder lançar. Nada aqui mexe em estoque.
/// </summary>
[ApiController]
[Route("api/gestao/catalogo")]
public sealed class GestaoCatalogoController(ICatalogoService service) : ControllerBase
{
    [HttpGet("produtos")]
    public Task<IActionResult> Produtos(CancellationToken ct) => Run(false, async (loja, _) => await service.Produtos(loja, ct));

    [HttpPost("produtos")]
    public Task<IActionResult> Criar([FromBody] CriarProdutoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Criar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPut("produtos/{id:guid}/finalidades")]
    public Task<IActionResult> Finalidades(Guid id, [FromBody] FinalidadesRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Finalidades(loja, usuario, id, req?.Finalidades, ct));

    [HttpGet("global")]
    public Task<IActionResult> Global([FromQuery] string? busca, CancellationToken ct) => Run(false, async (loja, _) =>
        await service.Global(loja, busca, ct));

    [HttpPost("global/{id:guid}/importar")]
    public Task<IActionResult> Importar(Guid id, [FromBody] ImportarProdutoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Importar(loja, usuario, id, req?.Finalidades, ct));

    [HttpPost("kits/{id:guid}/importar")]
    public Task<IActionResult> ImportarKit(Guid id, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.ImportarKit(loja, usuario, id, ct));

    [HttpGet("de-para-compras")]
    public Task<IActionResult> DeParaCompras(CancellationToken ct) => Run(false, async (loja, _) => await service.DeParaCompras(loja, ct));

    [HttpPost("de-para-compras/vincular")]
    public Task<IActionResult> Vincular([FromBody] VincularDeParaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Vincular(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPost("de-para-compras/nao-controlar")]
    public Task<IActionResult> NaoControlar([FromBody] NaoControlarRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.NaoControlar(loja, usuario, req ?? throw Vazio(), ct));

    [HttpPost("de-para-compras/{vinculoId:guid}/sugerir")]
    public Task<IActionResult> Sugerir(Guid vinculoId, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.Sugerir(loja, usuario, vinculoId, ct));

    [HttpGet("busca")]
    public Task<IActionResult> Buscar([FromQuery] string? texto, CancellationToken ct) => Run(false, async (loja, _) =>
        await service.Buscar(loja, texto, ct));

    private async Task<IActionResult> Run(bool escreve, Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente) throw new GestaoException("SEM_PERMISSAO", "O Catálogo é do gerente.", 403);
            if (escreve && !acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você só pode consultar o Catálogo.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);
}
