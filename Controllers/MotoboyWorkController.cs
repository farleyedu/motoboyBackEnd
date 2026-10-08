using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.DTOs.Delivery;
using APIBack.Extensions;
using APIBack.Model.Auth;
using APIBack.Service;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

[ApiController,Route("api/motoboys/me/work"),APIBack.Attributes.Authorize]
public sealed class MotoboyWorkController(MotoboyWorkService service) : ControllerBase
{
    private int? Owner => HttpContext.Items["JwtPayload"] is JwtPayload p && !p.MotoboySessionId.HasValue ? HttpContext.GetUserId() : null;
    [HttpGet]
    public Task<IActionResult> Read(Guid store,DateTimeOffset from,DateTimeOffset to,CancellationToken ct) => Run(store,async(r,u)=>await service.Read(store,r,from,to,ct),ct);
    [HttpPost("settlements/{id:guid}/actions")]
    public Task<IActionResult> Action(Guid id,Guid store,RiderSettlementAction request,CancellationToken ct) => Run(store,async(r,u)=>await service.Action(store,r,u,id,request,true,ct),ct);
    [HttpPost("support")]
    public Task<IActionResult> Support(Guid store,RiderSupportRequest request,CancellationToken ct) => Run(store,async(r,u)=>await service.Support(store,r,request,ct),ct);
    private async Task<IActionResult> Run(Guid store,Func<int,int,Task<object>> action,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        if(Owner is not >0) return Unauthorized(ApiResponse<object>.Fail("Entre na sua conta para consultar ganhos e acertos."));
        try { return Ok(ApiResponse<object>.Ok(await action(await service.OwnRider(Owner.Value,store,ct),Owner.Value))); }
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code));}
        catch(ArgumentException ex){return BadRequest(ApiResponse<object>.Fail(ex.Message,"VALIDATION_ERROR"));}
    }
}

[ApiController,Route("api/v2/delivery/rider-work")]
public sealed class RiderWorkAdminController(MotoboyWorkService service) : ControllerBase
{
    [HttpGet("{rider:int}"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Read(int rider,DateTimeOffset from,DateTimeOffset to,CancellationToken ct) => Run((s,u)=>service.Read(s,rider,from,to,ct));
    [HttpPost("{rider:int}/plans"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Plan(int rider,RiderPayPlanRequest request,CancellationToken ct) => Run(async(s,u)=>await service.SavePlan(s,rider,u,request,ct));
    [HttpPost("{rider:int}/periods"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Period(int rider,RiderPeriodRequest request,CancellationToken ct) => Run(async(s,u)=>await service.AddPeriod(s,rider,u,request,ct));
    [HttpPost("{rider:int}/backfill"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Backfill(int rider,CancellationToken ct) => Run(async(s,u)=>await service.Backfill(s,rider,u,ct));
    [HttpPost("{rider:int}/settlements"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Settlement(int rider,RiderSettlementRequest request,CancellationToken ct) => Run(async(s,u)=>await service.CreateSettlement(s,rider,u,request.OperationId,ct));
    [HttpPost("{rider:int}/settlements/{id:guid}/actions"),RequirePermission("Delivery","gestao_motoboy")]
    public Task<IActionResult> Action(int rider,Guid id,RiderSettlementAction request,CancellationToken ct) => Run(async(s,u)=>await service.Action(s,rider,u,id,request,false,ct));
    private async Task<IActionResult> Run(Func<Guid,int,Task<object>> action)
    {
        Response.Headers.CacheControl="no-store";
        if(HttpContext.GetEstabelecimentoId() is not Guid store || HttpContext.GetUserId() is not int user) return Unauthorized();
        try { return Ok(ApiResponse<object>.Ok(await action(store,user))); }
        catch(DeliveryDomainException ex){return StatusCode(ex.StatusCode,ApiResponse<object>.Fail(ex.Message,ex.Code));}
        catch(ArgumentException ex){return BadRequest(ApiResponse<object>.Fail(ex.Message,"VALIDATION_ERROR"));}
        catch(Npgsql.PostgresException ex) when(ex.SqlState==Npgsql.PostgresErrorCodes.UniqueViolation){return Conflict(ApiResponse<object>.Fail("Identificação reutilizada. Atualize e confira o registro.","WORK_CONFLICT"));}
    }
}
