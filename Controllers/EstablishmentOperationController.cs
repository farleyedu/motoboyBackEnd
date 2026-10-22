using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController]
[Route("api/configuracoes/estabelecimento/negocio/operacao")]
public sealed class EstablishmentOperationController(IOperacaoLojaService operacao) : ControllerBase
{
    [HttpGet]
    [RequirePermission("Configuracoes", "visualizar")]
    public Task<IActionResult> Status(CancellationToken cancellationToken) => Execute(id => operacao.StatusAsync(id, cancellationToken));

    /// <summary>Abre a loja ate HH:mm de hoje. Exige situacao teste/ativo e a prontidao obrigatoria ok (senao 422 com a lista).</summary>
    [HttpPost("abrir-hoje")]
    [RequirePermission("Configuracoes", "editar")]
    public Task<IActionResult> OpenToday(OpenStoreTodayRequest request, CancellationToken cancellationToken) =>
        Execute(id => operacao.AbrirHojeAsync(id, HttpContext.GetUserId()!.Value, request, cancellationToken));

    /// <summary>Fecha a loja agora. A grade semanal nao muda: o proximo turno volta ao normal.</summary>
    [HttpPost("fechar")]
    [RequirePermission("Configuracoes", "editar")]
    public Task<IActionResult> CloseNow(CancellationToken cancellationToken) =>
        Execute(id => operacao.FecharAsync(id, HttpContext.GetUserId()!.Value, cancellationToken));

    private async Task<IActionResult> Execute(Func<Guid, Task<StoreOperationDto>> action)
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!HttpContext.GetUserId().HasValue) return Unauthorized(ApiResponse<StoreOperationDto>.Fail("Não autorizado.", "UNAUTHENTICATED"));
        if (!id.HasValue || id.Value == Guid.Empty) return BadRequest(ApiResponse<StoreOperationDto>.Fail("Selecione um estabelecimento.", "ESTABLISHMENT_REQUIRED"));
        Response.Headers.CacheControl = "no-store";
        try { return Ok(ApiResponse<StoreOperationDto>.Ok(await action(id.Value))); }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<StoreOperationDto>.Fail(ex.Message, ex.Code, ex.Details)); }
    }
}
