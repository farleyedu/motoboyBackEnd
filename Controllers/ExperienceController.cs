using System.ComponentModel.DataAnnotations;
using APIBack.DTOs.Common;
using APIBack.DTOs.Experience;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController]
public sealed class ExperienceController(ExperienceService service, ILogger<ExperienceController> logger) : GestaoControllerBase
{
    [AllowAnonymous]
    [HttpGet("api/experience/location")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetLocation(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(ApiResponse<ExperienceLocationDto?>.Ok(await service.GetLocationAsync(cancellationToken)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Erro ao ler localização pública do Zippy Experience.");
            return StatusCode(500, ApiResponse<object>.Fail("Não foi possível carregar a localização do Zippy Experience."));
        }
    }

    [HttpPut("api/gestao/experience/location")]
    public async Task<IActionResult> SaveLocation([FromBody] ExperienceLocationDto location, CancellationToken cancellationToken)
    {
        if (!CurrentUserId.HasValue) return UnauthorizedResponse();
        try
        {
            var saved = await service.SaveLocationAsync(location, CurrentUserId.Value, CurrentIsSuperAdmin,
                HttpContext.GetEstabelecimentoNome(), HttpContext.GetEstabelecimentoModulosAtivos(),
                HasAnyPermission(("Usuarios", "visualizar"), ("Empresas", "visualizar"),
                    ("Estabelecimentos", "visualizar"), ("Configuracoes", "visualizar")), cancellationToken);
            return Ok(ApiResponse<ExperienceLocationDto>.Ok(saved));
        }
        catch (UnauthorizedAccessException)
        {
            return ForbiddenResponse();
        }
        catch (ValidationException exception)
        {
            return BadRequest(ApiResponse<object>.Fail(exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Erro ao salvar localização do Zippy Experience por UserId={UserId}.", CurrentUserId);
            return StatusCode(500, ApiResponse<object>.Fail("Não foi possível salvar a localização do Zippy Experience."));
        }
    }
}
