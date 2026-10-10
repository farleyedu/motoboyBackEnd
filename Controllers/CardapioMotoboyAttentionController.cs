using APIBack.Attributes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Common;
using APIBack.Repository;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController]
[Route("api/cardapio/{estabelecimentoId:guid}/atencao-motoboy")]
public sealed class CardapioMotoboyAttentionController(CardapioMotoboyAttentionRepository repository) : EstabelecimentoScopedControllerBase
{
    [HttpGet]
    [RequirePermission("Cardapio", "visualizar")]
    public async Task<IActionResult> Read(Guid estabelecimentoId, CancellationToken ct)
    {
        var error = ValidateScope(estabelecimentoId);
        if (error != null) return error;
        return Ok(ApiResponse<CardapioMotoboyAttentionDto>.Ok(await repository.ReadAsync(estabelecimentoId, ct)));
    }

    [HttpPatch("{tipo}/{itemId:guid}")]
    [RequirePermission("Cardapio", "editar")]
    public async Task<IActionResult> Set(Guid estabelecimentoId, string tipo, Guid itemId, [FromBody] CardapioMotoboyAttentionRequest request, CancellationToken ct)
    {
        var error = ValidateScope(estabelecimentoId);
        if (error != null) return error;
        if (tipo is not ("produtos" or "adicionais") || request.Atencao == null) return BadRequestErrorResponse("Informe o tipo de item e a seleção de atenção.");
        if (!await repository.SetAsync(estabelecimentoId, tipo, itemId, request.Atencao.Value, ct)) return NotFoundErrorResponse("Item não encontrado neste estabelecimento.");
        return Ok(ApiResponse<object>.Ok(new { atencao = request.Atencao.Value }));
    }

    private IActionResult? ValidateScope(Guid store)
    {
        if (!TryResolveCurrentEstabelecimentoAndModule("Cardapio", out var current, out var error)) return error;
        return current == store ? null : StatusCode(403, ApiResponse<object>.Fail("Este estabelecimento não pertence à sessão atual."));
    }
}
