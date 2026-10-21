using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Gestao;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

/// <summary>
/// Tela de operacao entre lojas da empresa. Fica fora de /api/v2/delivery de proposito: aquele prefixo e travado para loja em
/// implantacao e exige o plano da loja do token, e esta visao enxerga varias lojas (cada contador respeita os modulos da sua loja).
/// </summary>
[ApiController]
[Route("api/gestao/operacao")]
public sealed class GestaoOperacaoController(IGestaoOperacaoService service, ILogger<GestaoOperacaoController> logger) : GestaoControllerBase
{
    [HttpGet("visao-geral")]
    [RequirePermission("Delivery", "visualizar")]
    [ProducesResponseType(typeof(ApiResponse<VisaoGeralOperacaoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> VisaoGeral(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentEmpresaId.HasValue)
            return BadRequest(ApiResponse<object>.Fail("Selecione uma empresa para ver a operacao das lojas.", "EMPRESA_REQUIRED"));
        try
        {
            var result = await service.VisaoGeralAsync(CurrentUserId.Value, CurrentEmpresaId.Value, CurrentCompanyRole, CurrentIsSuperAdmin,
                HasAnyPermission(("Delivery", "integracao_ifood")), ct);
            return Ok(ApiResponse<VisaoGeralOperacaoDto>.Ok(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Erro ao montar a visao geral da operacao. UserId={UserId} EmpresaId={EmpresaId}", CurrentUserId, CurrentEmpresaId);
            return StatusCode(500, ApiResponse<object>.Fail("Erro ao carregar a operacao das lojas."));
        }
    }
}
