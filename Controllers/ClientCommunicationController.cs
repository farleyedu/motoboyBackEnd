using APIBack.Attributes;
using APIBack.DTOs.Atendimento;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

[ApiController,Route("api/v2/motoboys/me/session/orders/{pedidoId:int}/client-chat"),RequireOperationalSession]
public sealed class ClientCommunicationController(ClientCommunicationService service):ControllerBase
{
    private ChatActor Actor=>new(HttpContext.GetEstabelecimentoId()??Guid.Empty,HttpContext.GetUserId()??0,HttpContext.GetJwtPayload().MotoboyId);
    [HttpGet]
    public Task<IActionResult> History(int pedidoId,DateTime? before,int limit=50)=>Run(()=>service.ListAsync(Actor,pedidoId,before,limit));
    [HttpPost]
    public Task<IActionResult> Send(int pedidoId,SendCommunicationRequest request)=>Run(()=>service.SendAsync(Actor,pedidoId,request));
    private async Task<IActionResult> Run<T>(Func<Task<T>> work)
    {
        try{return Ok(ApiResponse<T>.Ok(await work()));}
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code));}
        catch(PostgresException ex)when(ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn){return StatusCode(503,ApiResponse<object>.Fail("Comunicacao nao habilitada neste ambiente.","MIGRATION_PENDING"));}
    }
}
