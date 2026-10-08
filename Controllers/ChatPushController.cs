using APIBack.Attributes;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace APIBack.Controllers;
[ApiController,Route("api/v2/motoboys/me/session/chat/push"),RequireOperationalSession]
public sealed class ChatPushController(ChatPushService service):ControllerBase
{
    [HttpPut]public async Task<IActionResult> Register(ChatPushRequest request)
    {
        try{var jwt=HttpContext.GetJwtPayload();await service.RegisterAsync(new(HttpContext.GetEstabelecimentoId()??Guid.Empty,HttpContext.GetUserId()??0,jwt.MotoboyId),jwt.MotoboySessionId!.Value,request);return Ok(ApiResponse<object>.Ok(new{}));}
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code));}
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.UndefinedTable){return StatusCode(503,ApiResponse<object>.Fail("Notificacoes nao habilitadas.","MIGRATION_PENDING"));}
    }
}
