using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;
namespace APIBack.Controllers;

[ApiController,Route("api/motoboys/me/routes"),APIBack.Attributes.Authorize]
public sealed class MotoboyRouteHistoryController(MotoboyWorkService work,MotoboyRouteHistoryService history):ControllerBase
{
    [HttpGet] public Task<IActionResult> List(Guid store,DateTimeOffset from,DateTimeOffset to,int offset,CancellationToken ct)=>Run(store,r=>history.List(store,r,from,to,offset,ct),ct);
    [HttpGet("{id:guid}")] public Task<IActionResult> Detail(Guid id,Guid store,CancellationToken ct)=>Run(store,r=>history.Detail(store,r,id,ct),ct);
    private async Task<IActionResult> Run(Guid store,Func<int,Task<object>> action,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        if(HttpContext.Items["JwtPayload"] is not JwtPayload p || p.MotoboySessionId.HasValue || HttpContext.GetUserId() is not int owner) return Unauthorized(ApiResponse<object>.Fail("Entre na sua conta para consultar o histórico."));
        try{return Ok(ApiResponse<object>.Ok(await action(await work.OwnRider(owner,store,ct))));}
        catch(DeliveryDomainException e){return StatusCode(e.StatusCode,ApiResponse<object>.Fail(e.Message,e.Code));}
        catch(ArgumentException e){return BadRequest(ApiResponse<object>.Fail(e.Message,"VALIDATION_ERROR"));}
    }
}
