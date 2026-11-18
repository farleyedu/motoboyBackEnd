using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Mesas.Nucleo;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Funções da conta em cada loja, para o app da equipe escolher o modo depois do login neutro (<c>clientApp:'equipe'</c>).
/// Não exige loja no token: lista todas as lojas onde a pessoa é garçom, gerente do salão, cozinha, bar, caixa ou motoboy.
/// </summary>
[ApiController]
[Route("api/me/funcoes")]
[APIBack.Attributes.Authorize]
public sealed class MeFuncoesController(IMesasNucleoService nucleo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetUserId() is not int usuario) return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        return Ok(ApiResponse<IReadOnlyList<MinhaLojaFuncoesDto>>.Ok(await nucleo.MinhasFuncoesAsync(usuario, ct)));
    }
}
