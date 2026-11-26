using System.Threading;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Turnos;

/// <summary>Turno de entrega da loja do token: o mesmo número que o painel e o app mostram.</summary>
[ApiController, Route("api/v2/delivery/turno-entrega")]
public sealed class TurnoEntregaController : ControllerBase
{
    private readonly ITurnoEntregaService _turnos;

    public TurnoEntregaController(ITurnoEntregaService turnos) => _turnos = turnos;

    [HttpGet]
    [RequireModule("Delivery")]
    [RequirePermission("Delivery", "visualizar")]
    public async Task<IActionResult> Obter(CancellationToken ct)
    {
        if (HttpContext.GetEstabelecimentoId() is not { } loja)
            return BadRequest(ApiResponse<object>.Fail("Selecione um restaurante.", "ESTABELECIMENTO_NAO_SELECIONADO"));
        Response.Headers.CacheControl = "no-store";
        var situacao = await _turnos.SituacaoAsync(loja, ct);
        return situacao == null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.Fail("O turno de entrega ainda não foi habilitado neste ambiente.", "MIGRATION_PENDING"))
            : Ok(ApiResponse<TurnoEntregaDto>.Ok(TurnoEntregaService.ParaDto(situacao)));
    }
}
