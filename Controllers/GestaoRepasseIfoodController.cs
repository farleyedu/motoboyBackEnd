using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.RepasseIfood;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

/// <summary>
/// Recebíveis do iFood (CONTRATOS §9.10): o que falta o iFood repassar e a confirmação manual do repasse de um pedido. Só o gerente.
/// </summary>
[ApiController]
[Route("api/gestao/ifood")]
public sealed class GestaoRepasseIfoodController(NpgsqlDataSource fonte, IAcontecimentosService livro) : ControllerBase
{
    private readonly IRepasseIfoodService _service = new RepasseIfoodService(fonte, livro);

    [HttpGet("recebiveis")]
    public Task<IActionResult> Recebiveis(CancellationToken ct) => Run(async (loja, _) => await _service.AReceber(loja, ct));

    [HttpPost("liquidacoes")]
    public Task<IActionResult> Liquidar([FromBody] LiquidarRepasseIfoodRequest? req, CancellationToken ct) => Run(async (loja, usuario) =>
        await _service.Liquidar(loja, usuario, req ?? throw new GestaoException("CAMPO_INVALIDO", "Envie o repasse.", 422), ct));

    private async Task<IActionResult> Run(Func<Guid, int, Task<object?>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel != GestaoPapeis.Gerente || !acesso.PodeLancar)
                throw new GestaoException("SEM_PERMISSAO", "Só o gerente confere o repasse do iFood.", 403);
            return Ok(ApiResponse<object?>.Ok(await acao(loja, usuario)));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
