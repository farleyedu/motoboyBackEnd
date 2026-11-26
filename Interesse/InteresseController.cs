using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Interesse;

public sealed record RegistrarInteresseRequest(string? Tipo);

/// <summary>Interesse em créditos de motoboy: qualquer pessoa da loja registra; só o super admin lista.</summary>
[ApiController]
public sealed class InteresseController : GestaoControllerBase
{
    private readonly IInteresseService _interesse;

    public InteresseController(IInteresseService interesse) => _interesse = interesse;

    [HttpPost("api/v2/comercial/interesse")]
    public async Task<IActionResult> Registrar([FromBody] RegistrarInteresseRequest? request, CancellationToken ct)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentEstabelecimentoId.HasValue)
            return BadRequest(ApiResponse<object>.Fail("Selecione um estabelecimento.", "ESTABELECIMENTO_NAO_SELECIONADO"));
        if (request?.Tipo != TiposDeInteresse.AbriuCompra)
            return UnprocessableEntity(ApiResponse<object>.Fail("Tipo de interesse inválido.", "TIPO_INVALIDO"));
        // A equipe Zippy olhando a tela de uma loja não é interesse dela.
        if (CurrentIsSuperAdmin) return Ok(ApiResponse<object>.Ok(new { ok = true }));

        var papel = string.IsNullOrWhiteSpace(CurrentEstablishmentRole) ? CurrentCompanyRole : CurrentEstablishmentRole;
        return await _interesse.RegistrarAbriuCompraAsync(CurrentEstabelecimentoId.Value, CurrentUserId.Value, papel, ct)
            ? Ok(ApiResponse<object>.Ok(new { ok = true }))
            : StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.Fail("O registro ainda não foi habilitado neste ambiente.", "MIGRATION_PENDING"));
    }

    [HttpGet("api/gestao/interesse-motoboys")]
    public async Task<IActionResult> Listar([FromQuery] int? dias, [FromQuery] Guid? estabelecimentoId, CancellationToken ct)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentIsSuperAdmin) return ForbiddenResponse();
        Response.Headers.CacheControl = "no-store";
        var lista = await _interesse.ListarMotoboysAsync(dias ?? 30, estabelecimentoId, ct);
        return Ok(ApiResponse<IReadOnlyList<InteresseMotoboyDto>>.Ok(lista));
    }
}
