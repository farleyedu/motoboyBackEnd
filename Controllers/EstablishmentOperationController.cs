using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Extensions;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController]
[Route("api/configuracoes/estabelecimento/negocio/operacao")]
public sealed class EstablishmentOperationController(IHorarioOperacaoRepository horarios) : ControllerBase
{
    [HttpGet]
    [RequirePermission("Configuracoes", "visualizar")]
    public Task<IActionResult> Status(CancellationToken cancellationToken) => Execute(id => horarios.ObterEstadoAsync(id, cancellationToken));

    [HttpPost("abrir-hoje")]
    [RequirePermission("Configuracoes", "editar")]
    public Task<IActionResult> OpenToday(OpenStoreTodayRequest request, CancellationToken cancellationToken) =>
        Execute(id => horarios.AbrirHojeAsync(id, HttpContext.GetUserId()!.Value, request, cancellationToken));

    private async Task<IActionResult> Execute(Func<Guid, Task<StoreOperationDto>> action)
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!HttpContext.GetUserId().HasValue) return Unauthorized(ApiResponse<StoreOperationDto>.Fail("Não autorizado.", "UNAUTHENTICATED"));
        if (!id.HasValue || id.Value == Guid.Empty) return BadRequest(ApiResponse<StoreOperationDto>.Fail("Selecione um estabelecimento.", "ESTABLISHMENT_REQUIRED"));
        Response.Headers.CacheControl = "no-store";
        try { return Ok(ApiResponse<StoreOperationDto>.Ok(await action(id.Value))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<StoreOperationDto>.Fail(ex.Message, ex.Code)); }
    }
}
