using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

[ApiController, Route("api/v2/motoboys/me/session/offer/web-push"), RequireOperationalSession]
public sealed class WebOfferPushController(WebOfferPushService service) : ControllerBase
{
    [HttpGet("configuration")]
    public IActionResult Configuration() => Ok(ApiResponse<object>.Ok(new { configured = service.Configured, publicKey = service.PublicKey }));

    [HttpPut]
    public async Task<IActionResult> Register(WebPushRegistration request)
    {
        try
        {
            var jwt = HttpContext.GetJwtPayload();
            await service.RegisterAsync(new(HttpContext.GetEstabelecimentoId() ?? Guid.Empty, HttpContext.GetUserId() ?? 0, jwt.MotoboyId), jwt.MotoboySessionId!.Value, jwt.SessionEpoch!.Value, request);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (DeliveryDomainException ex) { return StatusCode(ex.StatusCode, ApiResponse<object>.Fail(ex.Message, ex.Code)); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return StatusCode(503, ApiResponse<object>.Fail("Os avisos ainda precisam ser habilitados pela equipe.", "MIGRATION_PENDING")); }
    }
}
