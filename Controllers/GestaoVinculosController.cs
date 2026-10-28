using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Vinculos;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record MotivoRequest(string? Motivo);

public sealed record MesclarRequest(Guid DestinoId);

/// <summary>
/// Vínculos de compras (CONTRATOS, seção "Lógica — vínculos"): sugestões por linha do documento, confirmação (vale na hora
/// para a loja), lista com busca e desfazer. Não mexe em estoque: a entrada só acontece na conferência da compra.
/// </summary>
[ApiController]
[Route("api/gestao")]
public sealed class GestaoVinculosController(IVinculosService service) : ControllerBase
{
    [HttpGet("compras/documentos/{id:guid}/sugestoes")]
    public Task<IActionResult> Sugestoes(Guid id, CancellationToken ct) => Run(async (loja, _) =>
        await service.Sugerir(loja, id, ct));

    [HttpPost("compras/documentos/{id:guid}/itens/{linha:int}/vinculo")]
    public Task<IActionResult> Confirmar(Guid id, int linha, [FromBody] ConfirmarVinculoRequest? req, CancellationToken ct) =>
        Run(async (loja, usuario) => await service.Confirmar(loja, usuario, id, linha,
            req ?? throw new GestaoException("CAMPO_INVALIDO", "Envie a escolha do vínculo.", 422), ct));

    [HttpGet("vinculos")]
    public Task<IActionResult> Listar([FromQuery] string? busca, [FromQuery] bool todos, CancellationToken ct) =>
        Run(async (loja, _) => await service.Listar(loja, busca, todos, ct));

    [HttpPost("vinculos/{id:guid}/desfazer")]
    public Task<IActionResult> Desfazer(Guid id, [FromBody] MotivoRequest? req, CancellationToken ct) =>
        Run(async (loja, usuario) => await service.Desfazer(loja, usuario, id, req?.Motivo, ct));

    // Vínculo de compra é do gerente que lança no Financeiro (o operador de caixa não vê Compras).
    private async Task<IActionResult> Run(Func<Guid, int, Task<object>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente || !acesso.PodeLancar)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente vincula compras.", 403);
            return Ok(ApiResponse<object>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}

/// <summary>Curadoria Zippy do catálogo global (só super admin). CONTRATOS, seção "Lógica — vínculos".</summary>
[ApiController]
[Route("api/gestao/curadoria")]
public sealed class GestaoCuradoriaController(ICuradoriaService service) : ControllerBase
{
    [HttpGet("candidatos")]
    public Task<IActionResult> Candidatos(CancellationToken ct) => Run(async _ => await service.Candidatos(ct));

    [HttpPost("{id:guid}/validar")]
    public Task<IActionResult> Validar(Guid id, [FromBody] ValidarCatalogoRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.Validar(usuario, id, req ?? new ValidarCatalogoRequest(null, null, null), ct));

    [HttpPost("{id:guid}/rejeitar")]
    public Task<IActionResult> Rejeitar(Guid id, [FromBody] MotivoRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.Rejeitar(usuario, id, req?.Motivo, ct));

    [HttpPost("{id:guid}/mesclar")]
    public Task<IActionResult> Mesclar(Guid id, [FromBody] MesclarRequest? req, CancellationToken ct) =>
        Run(async usuario => await service.Mesclar(usuario, id,
            req?.DestinoId ?? throw new GestaoException("CAMPO_INVALIDO", "Informe o item de destino.", 422), ct));

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
