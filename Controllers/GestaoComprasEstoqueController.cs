using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Compras;
using APIBack.GestaoLogica.Estoque;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record ContagensRequest(IReadOnlyList<Contagem>? Contagens);

public sealed record PedidosBaixaRequest(IReadOnlyList<PedidoBaixa>? Pedidos);

/// <summary>Compra a partir do documento e conferência por linha (CONTRATOS §9.5). Só o gerente que lança no Financeiro.</summary>
[ApiController]
[Route("api/gestao/compras")]
public sealed class GestaoComprasController(IComprasService service) : ControllerBase
{
    [HttpPost]
    public Task<IActionResult> Registrar([FromBody] RegistrarCompraRequest? req, CancellationToken ct) => Run(async (loja, usuario) =>
        await service.Registrar(loja, usuario, req ?? throw new GestaoException("CAMPO_INVALIDO", "Envie a compra.", 422), ct));

    [HttpGet("{documentoId:guid}")]
    public Task<IActionResult> Obter(Guid documentoId, CancellationToken ct) => Run(async (loja, _) =>
        await service.Obter(loja, documentoId, ct) ?? throw new GestaoException("NAO_ENCONTRADO", "Este documento ainda não virou compra.", 404));

    [HttpPost("{documentoId:guid}/itens/{linha:int}/conferencia")]
    public Task<IActionResult> Conferir(Guid documentoId, int linha, [FromBody] ConferirItemRequest? req, CancellationToken ct) =>
        Run(async (loja, usuario) => await service.Conferir(loja, usuario, documentoId, linha,
            req ?? throw new GestaoException("CAMPO_INVALIDO", "Envie a conferência.", 422), ct));

    private async Task<IActionResult> Run(Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente || !acesso.PodeLancar)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente lança e confere compras.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}

/// <summary>
/// Estoque (CONTRATOS §9.5): saldos e prévias são do gerente (o operador conta às cegas); perda, inventário e baixa por venda
/// também aceitam o operador com <c>Estoque:contar</c>, só no dia de hoje (o serviço confere pelo <see cref="GestaoPapeis"/>).
/// </summary>
[ApiController]
[Route("api/gestao/estoque")]
public sealed class GestaoEstoqueController(IEstoqueService service) : ControllerBase
{
    [HttpGet("saldos")]
    public Task<IActionResult> Saldos(CancellationToken ct) => Run(true, async (loja, _, _) => await service.Saldos(loja, ct));

    [HttpPost("perdas")]
    public Task<IActionResult> Perda([FromBody] PerdaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.RegistrarPerda(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("inventario/previa")]
    public Task<IActionResult> PreviaInventario([FromBody] ContagensRequest? req, CancellationToken ct) => Run(true, async (loja, _, _) =>
        await service.PreviaInventario(loja, req?.Contagens ?? Array.Empty<Contagem>(), ct));

    [HttpPost("inventario")]
    public Task<IActionResult> Inventario([FromBody] InventarioRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.RegistrarInventario(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("baixa-vendas/previa")]
    public Task<IActionResult> PreviaBaixa([FromBody] PedidosBaixaRequest? req, CancellationToken ct) => Run(true, async (loja, _, _) =>
        await service.PreviaBaixa(loja, req?.Pedidos ?? Array.Empty<PedidoBaixa>(), ct));

    [HttpPost("baixa-vendas")]
    public Task<IActionResult> Baixa([FromBody] BaixaVendasRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.RegistrarBaixa(loja, usuario, acesso, req ?? throw Vazio(), ct));

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);

    private async Task<IActionResult> Run(bool soGerente, Func<Guid, int, GestaoPapeis.Acesso, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (soGerente && acesso.Papel != GestaoPapeis.Gerente)
                throw new GestaoException("SEM_PERMISSAO", "Saldo e prévia são do gerente (o operador conta às cegas).", 403);
            if (acesso.Papel is null) throw new GestaoException("SEM_PERMISSAO", "Você não tem acesso à gestão desta loja.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario, acesso)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
