using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Vendas;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Produtos vendidos, de-para de vendas (iFood e cardápio) e ficha técnica por produto (6C3-A). Só o gerente, como o resto do Catálogo;
/// escrever exige poder lançar. Nada aqui move estoque nem cria evento financeiro.
/// </summary>
[ApiController]
[Route("api/gestao/catalogo")]
public sealed class GestaoVendasCatalogoController(IVendasCatalogoService service) : ControllerBase
{
    [HttpGet("produtos-vendidos")]
    public Task<IActionResult> ProdutosVendidos(CancellationToken ct) => Run(false, async (loja, _) => await service.ProdutosVendidos(loja, ct));

    [HttpGet("de-para-vendas")]
    public Task<IActionResult> DeParaVendas(CancellationToken ct) => Run(false, async (loja, _) => await service.DeParaVendas(loja, ct));

    [HttpPost("de-para-vendas/vincular")]
    public Task<IActionResult> Vincular([FromBody] VincularVendaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.VincularVenda(loja, usuario, req?.VinculoId, req?.ProdutoVendidoId, ct));

    [HttpGet("fichas/{produtoId:guid}")]
    public Task<IActionResult> Ficha(Guid produtoId, CancellationToken ct) => Run(false, async (loja, _) =>
        await service.Ficha(loja, produtoId, ct));

    [HttpPut("fichas/{produtoId:guid}")]
    public Task<IActionResult> SalvarFicha(Guid produtoId, [FromBody] SalvarFichaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario) =>
        await service.SalvarFicha(loja, usuario, produtoId, req?.Componentes, ct));

    private async Task<IActionResult> Run(bool escreve, Func<Guid, int, Task<object?>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente) throw new GestaoException("SEM_PERMISSAO", "O Catálogo é do gerente.", 403);
            if (escreve && !acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você só pode consultar o Catálogo.", 403);
            return Ok(ApiResponse<object?>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
