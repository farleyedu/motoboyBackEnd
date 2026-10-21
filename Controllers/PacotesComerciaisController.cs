using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController]
public sealed class PacotesComerciaisController(IPacotesComerciaisService service) : GestaoControllerBase
{
    [HttpGet("api/gestao/comercial/pacotes")]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentIsSuperAdmin) return ForbiddenResponse();
        return Ok(ApiResponse<System.Collections.Generic.IReadOnlyList<PacoteComercialDto>>.Ok(await service.ListarAsync(ct)));
    }

    [HttpPut("api/gestao/comercial/pacotes/{chave}")]
    public async Task<IActionResult> Atualizar(string chave, EditarPacoteComercialRequest request, CancellationToken ct)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        if (!CurrentIsSuperAdmin) return ForbiddenResponse();
        try
        {
            return Ok(ApiResponse<PacoteComercialDto>.Ok(await service.AtualizarAsync(chave, request, CurrentUserId.Value, ct)));
        }
        catch (SituacaoComercialException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }
}
