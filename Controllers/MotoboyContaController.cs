using APIBack.DTOs.Common;
using APIBack.DTOs.Motoboy;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using CustomAuthorize = APIBack.Attributes.AuthorizeAttribute;

namespace APIBack.Controllers;

[ApiController, Route("api/motoboys/me"), CustomAuthorize]
public sealed class MotoboyContaController(MotoboyContaService service) : ControllerBase
{
    // Token operacional não dá acesso à conta ou aos documentos pessoais.
    private int? Owner => HttpContext.Items["JwtPayload"] is JwtPayload payload && !payload.MotoboySessionId.HasValue ? HttpContext.GetUserId() : null;

    [HttpGet("perfil")]
    public Task<IActionResult> Profile(CancellationToken ct) => Run(async id => await service.GetAsync(id, ct));

    [HttpPatch("perfil")]
    public Task<IActionResult> UpdateProfile(MotoboyDadosRequest request, CancellationToken ct) => Run(async id => await service.UpdateDadosAsync(id, request, ct) ? await service.GetAsync(id, ct) : null);

    [HttpPut("veiculo")]
    public Task<IActionResult> UpdateVehicle(MotoboyVeiculoRequest request, CancellationToken ct) => Run(async id => await service.UpdateVeiculoAsync(id, request, ct) ? await service.GetAsync(id, ct) : null);

    [HttpGet("documentos")]
    public Task<IActionResult> Documents(CancellationToken ct) => Run(async id => await service.GetAsync(id, ct) == null ? null : await service.DocumentsAsync(id, ct));

    [HttpPost("documentos"), RequestSizeLimit(5_700_000)]
    public Task<IActionResult> Upload(MotoboyImagemRequest request, CancellationToken ct) => Run(async id => await service.SaveImageAsync(id, request, ct));

    [HttpGet("documentos/{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        if (Owner is not int owner) return Unauthorized(ApiResponse<object>.Fail("Entre na sua conta para ver seus documentos."));
        Response.Headers.CacheControl = "no-store";
        var bytes = await service.ReadImageAsync(owner, id, ct);
        return bytes == null ? NotFound(ApiResponse<object>.Fail("Documento não encontrado.")) : Ok(ApiResponse<string>.Ok("data:image/jpeg;base64," + Convert.ToBase64String(bytes)));
    }

    private async Task<IActionResult> Run(Func<int, Task<object?>> action)
    {
        if (Owner is not int id) return Unauthorized(ApiResponse<object>.Fail("Entre na sua conta para atualizar seu cadastro."));
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await action(id);
            return result == null ? NotFound(ApiResponse<object>.Fail("Cadastro de motoboy não encontrado.", "PROFILE_NOT_FOUND")) : Ok(ApiResponse<object>.Ok(result));
        }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<object>.Fail(ex.Message, "VALIDATION_ERROR")); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Conflict(ApiResponse<object>.Fail("Estes dados já estão cadastrados.", "PROFILE_CONFLICT")); }
    }
}
